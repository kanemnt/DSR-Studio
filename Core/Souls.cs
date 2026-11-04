using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DSRStudio.Core;

/// <summary>DCX 容器（DFLT/zlib）。DSR 全部参数、文本、贴图容器都用它。</summary>
public static class Dcx
{
    public static bool IsDcx(byte[] b) => b.Length > 4 && b[0] == (byte)'D' && b[1] == (byte)'C' && b[2] == (byte)'X';

    public static byte[] Decompress(byte[] b)
    {
        if (!IsDcx(b)) return b;
        int dcs = IndexOf(b, "DCS\0");
        int dcp = IndexOf(b, "DCP\0");
        int dca = IndexOf(b, "DCA\0");
        if (dcs < 0 || dca < 0) throw new InvalidDataException("DCX 头损坏");
        int unc = ReadBe(b, dcs + 4);
        int comp = ReadBe(b, dcs + 8);
        string method = Encoding.ASCII.GetString(b, dcp + 4, 4);
        if (method != "DFLT") throw new NotSupportedException("不支持的压缩算法: " + method);
        int hdr = ReadBe(b, dca + 4);
        int start = dca + hdr;
        var payload = new byte[comp];
        Array.Copy(b, start, payload, 0, Math.Min(comp, b.Length - start));
        using var ms = new MemoryStream(payload);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        var outBuf = new byte[unc];
        int read = 0;
        while (read < unc)
        {
            int n = z.Read(outBuf, read, unc - read);
            if (n <= 0) break;
            read += n;
        }
        return outBuf;
    }

    /// <summary>用原始 DCX 头作为模板重新压缩（保留 DCS/DCP/DCA 头，仅改长度字段）。</summary>
    public static byte[] Compress(byte[] raw, byte[] template)
    {
        if (!IsDcx(template)) return raw;
        int dcs = IndexOf(template, "DCS\0");
        int dca = IndexOf(template, "DCA\0");
        int hdr = ReadBe(template, dca + 4);
        int payloadOff = dca + hdr;

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        var comp = ms.ToArray();

        var header = new byte[payloadOff];
        Array.Copy(template, header, payloadOff);
        WriteBe(header, dcs + 4, raw.Length);
        WriteBe(header, dcs + 8, comp.Length);

        var result = new byte[payloadOff + comp.Length];
        Buffer.BlockCopy(header, 0, result, 0, payloadOff);
        Buffer.BlockCopy(comp, 0, result, payloadOff, comp.Length);
        return result;
    }

    public static int IndexOf(byte[] b, string ascii)
    {
        var pat = Encoding.ASCII.GetBytes(ascii);
        for (int i = 0; i + pat.Length <= b.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < pat.Length; k++) if (b[i + k] != pat[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    static int ReadBe(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}

public sealed class BndEntry
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int Offset { get; init; }
    public int Size { get; init; }
}

/// <summary>BND3 容器（DSR 的 *.bnd / *.msgbnd 等）。</summary>
public static class Bnd3
{
    public static List<BndEntry> Entries(byte[] d)
    {
        var list = new List<BndEntry>();
        if (d.Length < 0x20) return list;
        int count = BitConverter.ToInt32(d, 0x10);
        if (count < 0 || count > 100000) return list;
        for (int i = 0; i < count; i++)
        {
            int o = 0x20 + i * 24;
            if (o + 24 > d.Length) break;
            int size = BitConverter.ToInt32(d, o + 4);
            int off = BitConverter.ToInt32(d, o + 8);
            int nameOff = BitConverter.ToInt32(d, o + 16);
            string name = "";
            if (nameOff > 0 && nameOff < d.Length)
            {
                int e = nameOff;
                while (e < d.Length && d[e] != 0) e++;
                name = Encoding.UTF8.GetString(d, nameOff, e - nameOff);
                name = name.Replace('\\', '/');
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name[(slash + 1)..];
            }
            if (off < 0 || off + size > d.Length) continue;
            list.Add(new BndEntry { Index = i, Name = name, Offset = off, Size = size });
        }
        return list;
    }
}

public sealed class FmgEntry
{
    public uint Id { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public string Text { get; set; } = "";
    public int ByteLength => End - Start;
}

/// <summary>FMG 文本表（UTF-16LE，字符串池按偏移索引）。</summary>
public static class Fmg
{
    public static List<FmgEntry> Parse(byte[] d, int off, int size)
    {
        var list = new List<FmgEntry>();
        if (off < 0 || off + size > d.Length || size < 0x1C) return list;
        int strCount = BitConverter.ToInt32(d, off + 0x0C);
        int offTable = BitConverter.ToInt32(d, off + 0x14);
        if (strCount <= 0 || strCount > 100000 || offTable <= 0) return list;

        var offsets = new int[strCount];
        for (int i = 0; i < strCount; i++)
        {
            int p = off + offTable + i * 4;
            if (p + 4 > d.Length) break;
            offsets[i] = BitConverter.ToInt32(d, p);
        }

        int pos = off + 0x1C;
        int limit = off + offTable;
        var seen = new HashSet<uint>();
        while (pos + 12 <= limit)
        {
            int strIndex = BitConverter.ToInt32(d, pos);
            int firstId = BitConverter.ToInt32(d, pos + 4);
            int lastId = BitConverter.ToInt32(d, pos + 8);
            pos += 12;
            if (firstId < 0 || lastId < firstId || lastId - firstId > 100000) continue;
            for (int k = 0; k <= lastId - firstId; k++)
            {
                int si = strIndex + k;
                if (si < 0 || si >= strCount) continue;
                int start = off + offsets[si];
                int end = start;
                int max = off + size;
                while (end + 1 < max && !(d[end] == 0 && d[end + 1] == 0)) end += 2;
                uint id = (uint)(firstId + k);
                if (!seen.Add(id)) continue;
                list.Add(new FmgEntry
                {
                    Id = id, Start = start, End = end,
                    Text = Encoding.Unicode.GetString(d, start, Math.Max(0, end - start))
                });
            }
        }
        return list;
    }

    /// <summary>等长（或更短）原地替换；成功返回 true。返回缓冲区需重新压缩回 DCX。</summary>
    public static bool Replace(byte[] d, FmgEntry e, string newText, out string error)
    {
        error = "";
        var bytes = Encoding.Unicode.GetBytes(newText);
        int oldLen = e.End - e.Start;
        if (bytes.Length > oldLen)
        {
            error = $"新文本需要 {bytes.Length} 字节，原文只有 {oldLen} 字节（仅支持等长或更短）";
            return false;
        }
        Buffer.BlockCopy(bytes, 0, d, e.Start, bytes.Length);
        for (int i = e.Start + bytes.Length; i < e.End; i++) d[i] = 0;
        return true;
    }
}

public sealed class TpfTexture
{
    public int Offset;
    public int DataOffset;
    public int Width;
    public int Height;
    public string FourCC = "";
    public int Dxgi = -1;
    public int MipSize;

    public string DxgiName => Dxgi switch
    {
        70 or 71 or 72 => "BC1/DXT1 (DX10)",
        73 or 74 or 75 => "BC2/DXT3 (DX10)",
        76 or 77 or 78 => "BC3/DXT5 (DX10)",
        79 or 80 or 81 => "BC4 (DX10)",
        82 or 83 or 84 => "BC5 (DX10)",
        95 or 96 or 97 or 98 or 99 => "BC7 (DX10)",
        27 or 28 or 29 => "RGBA8 (DX10)",
        87 or 91 or 93 => "BGRA8 (DX10)",
        60 or 61 => "R8 (DX10)",
        _ => $"DX10/{Dxgi}"
    };

    public string Format => FourCC switch
    {
        "DXT1" => "BC1 / DXT1",
        "DXT3" => "BC2 / DXT3",
        "DXT5" => "BC3 / DXT5",
        "DX10" => DxgiName,
        _ => "未压缩 / 未知"
    };

    public string SizeText => $"{Width}×{Height}";
}

/// <summary>TPF 纹理包（DSR 的 *.tpf / *.tpf.dcx）。BC1/BC2/BC3/BC4/BC5 与常见 DX10 格式可预览。</summary>
public static class Tpf
{
    public static string LastError = "";

    public static List<TpfTexture> Textures(byte[] d, byte[] raw)
    {
        var list = new List<TpfTexture>();
        ReadOnlySpan<byte> span = d;
        int pos = 0;
        while (pos + 4 <= d.Length)
        {
            int rel = span.Slice(pos).IndexOf("DDS "u8);
            if (rel < 0) break;
            int i = pos + rel;
            pos = i + 4;
            if (i + 148 > d.Length) break;

            int height = BitConverter.ToInt32(d, i + 12);
            int width = BitConverter.ToInt32(d, i + 16);
            if (width < 1 || height < 1 || width > 16384 || height > 16384) continue;

            string cc = Encoding.ASCII.GetString(d, i + 84, 4);
            int dataOffset = i + 128;
            int dxgi = -1;
            int blockBytes = 0;

            if (cc == "DX10")
            {
                if (i + 148 > d.Length) continue;
                dxgi = BitConverter.ToInt32(d, i + 128);
                dataOffset = i + 148;
                blockBytes = dxgi switch
                {
                    70 or 71 or 72 => 8,          // BC1
                    73 or 74 or 75 => 16,         // BC2
                    76 or 77 or 78 => 16,         // BC3
                    79 or 80 or 81 => 8,          // BC4
                    82 or 83 or 84 => 16,         // BC5
                    95 or 96 or 97 or 98 or 99 => 16 // BC7
                };
            }
            else
            {
                blockBytes = cc switch { "DXT1" => 8, "DXT3" or "DXT5" => 16, _ => 0 };
            }

            int mip;
            if (blockBytes > 0)
            {
                int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
                mip = bw * bh * blockBytes;
            }
            else
            {
                mip = dxgi switch
                {
                    27 or 28 or 29 or 87 or 91 or 93 => width * height * 4,
                    60 or 61 => width * height,
                    _ => width * height * 4
                };
            }

            if (dataOffset + mip > d.Length)
            {
                // 数据被截断：记录下来但不中断扫描
                System.Diagnostics.Debug.WriteLine($"[Tpf] 跳过截断纹理 {width}x{height} @0x{i:X}");
                continue;
            }
            list.Add(new TpfTexture
            {
                Offset = i, DataOffset = dataOffset, Width = width, Height = height,
                FourCC = cc, Dxgi = dxgi, MipSize = mip
            });
        }
        return list;
    }

    /// <summary>解码首级 mip 为 BGRA32 像素。不支持的格式返回 null 并写入 LastError。</summary>
    public static byte[]? Decode(byte[] d, TpfTexture t)
    {
        LastError = "";
        try
        {
            int p = t.DataOffset;
            if (t.FourCC == "DXT1") return DecodeBc(d, p, t.Width, t.Height, true);
            if (t.FourCC == "DXT3") return DecodeBc(d, p, t.Width, t.Height, false);
            if (t.FourCC == "DXT5") return DecodeBc(d, p, t.Width, t.Height, false);

            if (t.FourCC == "DX10")
            {
                switch (t.Dxgi)
                {
                    case 70 or 71 or 72: return DecodeBc(d, p, t.Width, t.Height, true);
                    case 73 or 74 or 75: return DecodeBc(d, p, t.Width, t.Height, false);
                    case 76 or 77 or 78: return DecodeBc(d, p, t.Width, t.Height, false);
                    case 82 or 83 or 84: return DecodeBc5(d, p, t.Width, t.Height);
                    case 79 or 80 or 81: return DecodeBc4(d, p, t.Width, t.Height);
                    case 27 or 28 or 29: return CopyRgba(d, p, t.Width, t.Height, rgba: true);
                    case 87 or 91 or 93: return CopyRgba(d, p, t.Width, t.Height, rgba: false);
                    case 60 or 61: return CopyGray(d, p, t.Width, t.Height);
                    case 95 or 96 or 97 or 98 or 99:
                        return DecodeBc7(d, p, t.Width, t.Height, t.MipSize);
                    default:
                        LastError = "DXGI 格式 " + t.Dxgi + " 暂无解码器";
                        return null;
                }
            }

            LastError = "格式 " + t.FourCC + " 暂无解码器";
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    static byte[] CopyRgba(byte[] d, int p, int w, int h, bool rgba)
    {
        var outBuf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int s = p + i * 4, o = i * 4;
            if (s + 4 > d.Length) break;
            if (rgba)
            {
                outBuf[o] = d[s + 2];
                outBuf[o + 1] = d[s + 1];
                outBuf[o + 2] = d[s];
                outBuf[o + 3] = d[s + 3];
            }
            else
            {
                outBuf[o] = d[s];
                outBuf[o + 1] = d[s + 1];
                outBuf[o + 2] = d[s + 2];
                outBuf[o + 3] = d[s + 3];
            }
        }
        return outBuf;
    }

    static byte[] CopyGray(byte[] d, int p, int w, int h)
    {
        var outBuf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int s = p + i;
            if (s >= d.Length) break;
            byte v = d[s];
            int o = i * 4;
            outBuf[o] = v; outBuf[o + 1] = v; outBuf[o + 2] = v; outBuf[o + 3] = 255;
        }
        return outBuf;
    }

    /// <summary>BC4：单通道（alpha 块）。</summary>
    static byte[] DecodeBc4(byte[] d, int p, int w, int h)
    {
        var outBuf = new byte[w * h * 4];
        for (int i = 3; i < outBuf.Length; i += 4) outBuf[i] = 255;
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int o = p + (by * bw + bx) * 8;
                if (o + 8 > d.Length) continue;
                var al = AlphaBlock(d, o);
                for (int px = 0; px < 4; px++)
                    for (int py = 0; py < 4; py++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        byte v = al[(py * 4 + px)];
                        int dst = (y * w + x) * 4;
                        outBuf[dst] = v; outBuf[dst + 1] = v; outBuf[dst + 2] = v; outBuf[dst + 3] = 255;
                    }
            }
        return outBuf;
    }

    /// <summary>BC5：两通道（R/G），B 置 0。</summary>
    static byte[] DecodeBc5(byte[] d, int p, int w, int h)
    {
        var outBuf = new byte[w * h * 4];
        for (int i = 3; i < outBuf.Length; i += 4) outBuf[i] = 255;
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int o = p + (by * bw + bx) * 16;
                if (o + 16 > d.Length) continue;
                var rr = AlphaBlock(d, o);
                var gg = AlphaBlock(d, o + 8);
                for (int px = 0; px < 4; px++)
                    for (int py = 0; py < 4; py++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        int idx = py * 4 + px;
                        int dst = (y * w + x) * 4;
                        outBuf[dst] = gg[idx];
                        outBuf[dst + 1] = rr[idx];
                        outBuf[dst + 2] = 0;
                        outBuf[dst + 3] = 255;
                    }
            }
        return outBuf;
    }

    [System.Runtime.InteropServices.DllImport("bcdec_wrap.dll",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern int dsr_bc7_decompress(byte[] src, int srcLen, byte[] dst, int w, int h);

    /// <summary>BC7（BPTC）：调用自建原生解码器（bcdec，公共领域），输出 BGRA32。</summary>
    static byte[]? DecodeBc7(byte[] d, int p, int w, int h, int size)
    {
        try
        {
            int len = Math.Min(size, Math.Max(0, d.Length - p));
            if (len < 16) { LastError = "BC7 数据不足"; return null; }

            var slice = new byte[len];
            Buffer.BlockCopy(d, p, slice, 0, len);

            var outBuf = new byte[w * h * 4];
            int rc = dsr_bc7_decompress(slice, len, outBuf, w, h);
            if (rc != 0) { LastError = "BC7 解码器返回码 " + rc; return null; }
            return outBuf;
        }
        catch (DllNotFoundException)
        {
            LastError = "缺少 bcdec_wrap.dll（应与程序同目录）";
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            LastError = "bcdec_wrap.dll 导出符号不匹配";
            return null;
        }
        catch (Exception ex)
        {
            LastError = "BC7 解码失败: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    static byte[] AlphaBlock(byte[] d, int o)
    {
        var al = new byte[8];
        int a0 = d[o], a1 = d[o + 1];
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)d[o + 2 + i] << (8 * i);
        al[0] = (byte)a0;
        al[1] = (byte)a1;
        if (a0 > a1)
            for (int i = 1; i <= 6; i++) al[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (int i = 1; i <= 4; i++) al[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            al[6] = 0;
            al[7] = 255;
        }
        var outAl = new byte[16];
        for (int i = 0; i < 16; i++) outAl[i] = al[(int)((bits >> (3 * i)) & 7)];
        return outAl;
    }


    static byte[] DecodeBc(byte[] src, int baseOff, int w, int h, bool bc1)
    {
        var outBuf = new byte[w * h * 4];
        for (int i = 3; i < outBuf.Length; i += 4) outBuf[i] = 255;

        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        int blockSize = bc1 ? 8 : 16;

        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int o = baseOff + (by * bw + bx) * blockSize;
                if (o < 0 || o + blockSize > src.Length) continue;

                int colorOff = bc1 ? o : o + 8;
                if (colorOff + 8 > src.Length) continue;

                if (!bc1)
                {
                    var alpha = AlphaBlock(src, o);
                    for (int px = 0; px < 4; px++)
                        for (int py = 0; py < 4; py++)
                        {
                            int x = bx * 4 + px, y = by * 4 + py;
                            if (x >= w || y >= h) continue;
                            outBuf[(y * w + x) * 4 + 3] = alpha[py * 4 + px];
                        }
                }

                int c0 = src[colorOff] | (src[colorOff + 1] << 8);
                int c1 = src[colorOff + 2] | (src[colorOff + 3] << 8);
                uint idx = (uint)(src[colorOff + 4] | (src[colorOff + 5] << 8) |
                                  (src[colorOff + 6] << 16) | (src[colorOff + 7] << 24));

                var cr = new int[4];
                var cg = new int[4];
                var cb = new int[4];
                Unpack565((ushort)c0, out int r0, out int g0, out int b0);
                Unpack565((ushort)c1, out int r1, out int g1, out int b1);
                cr[0] = r0; cg[0] = g0; cb[0] = b0;
                cr[1] = r1; cg[1] = g1; cb[1] = b1;
                if (c0 > c1 || !bc1)
                {
                    cr[2] = (2 * r0 + r1) / 3; cg[2] = (2 * g0 + g1) / 3; cb[2] = (2 * b0 + b1) / 3;
                    cr[3] = (r0 + 2 * r1) / 3; cg[3] = (g0 + 2 * g1) / 3; cb[3] = (b0 + 2 * b1) / 3;
                }
                else
                {
                    cr[2] = (r0 + r1) / 2; cg[2] = (g0 + g1) / 2; cb[2] = (b0 + b1) / 2;
                    cr[3] = 0; cg[3] = 0; cb[3] = 0;
                }

                for (int px = 0; px < 4; px++)
                    for (int py = 0; py < 4; py++)
                    {
                        int ci = (int)((idx >> (2 * (py * 4 + px))) & 3);
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        int dst = (y * w + x) * 4;
                        outBuf[dst] = (byte)cb[ci];
                        outBuf[dst + 1] = (byte)cg[ci];
                        outBuf[dst + 2] = (byte)cr[ci];
                        if (bc1) outBuf[dst + 3] = 255;
                    }
            }
        }
        return outBuf;
    }

    static void Unpack565(ushort c, out int r, out int g, out int b)
    {
        int ri = (c >> 11) & 0x1F, gi = (c >> 5) & 0x3F, bi = c & 0x1F;
        r = (ri << 3) | (ri >> 2);
        g = (gi << 2) | (gi >> 4);
        b = (bi << 3) | (bi >> 2);
    }

    // ---------------- DDS 读写（供导入/导出） ----------------

    public static int BlockBytesOf(string fourCC, int dxgi) => fourCC switch
    {
        "DXT1" => 8,
        "DXT3" or "DXT5" => 16,
        "DX10" => dxgi switch
        {
            70 or 71 or 72 => 8,
            73 or 74 or 75 => 16,
            76 or 77 or 78 => 16,
            79 or 80 or 81 => 8,
            82 or 83 or 84 => 16,
            95 or 96 or 97 or 98 or 99 => 16,
            _ => 0
        },
        _ => 0
    };

    /// <summary>整条 mip 链的字节数（mip0 起逐级 /4，最小 1 块）。</summary>
    public static int ChainSize(int w, int h, int blockBytes, bool uncompressed)
    {
        if (blockBytes == 0 && !uncompressed) return 0;
        int total = 0;
        int cw = w, ch = h;
        while (true)
        {
            if (blockBytes > 0)
            {
                int bw = Math.Max(1, (cw + 3) / 4), bh = Math.Max(1, (ch + 3) / 4);
                total += bw * bh * blockBytes;
            }
            else total += cw * ch * 4;
            if (cw == 1 && ch == 1) break;
            cw = Math.Max(1, cw / 2);
            ch = Math.Max(1, ch / 2);
        }
        return total;
    }

    public sealed class DdsInfo
    {
        public int Width;
        public int Height;
        public string FourCC = "";
        public int Dxgi = -1;
        public int DataOffset;
        public int DataLength;
    }

    public static DdsInfo? ParseDds(byte[] d)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
        int height = BitConverter.ToInt32(d, 12);
        int width = BitConverter.ToInt32(d, 16);
        string cc = Encoding.ASCII.GetString(d, 84, 4);
        var info = new DdsInfo { Width = width, Height = height, FourCC = cc };
        int off = 128;
        if (cc == "DX10")
        {
            if (d.Length < 148) return null;
            info.Dxgi = BitConverter.ToInt32(d, 128);
            off = 148;
        }
        info.DataOffset = off;
        info.DataLength = d.Length - off;
        return info;
    }

    /// <summary>把纹理像素数据打包成标准 DDS（供 PS/Paint.NET 编辑）。</summary>
    public static byte[] BuildDds(int w, int h, string fourCC, int dxgi, byte[] pixelData)
    {
        bool dx10 = fourCC == "DX10";
        int headerLen = dx10 ? 148 : 128;
        var o = new byte[headerLen + pixelData.Length];

        o[0] = (byte)'D'; o[1] = (byte)'D'; o[2] = (byte)'S'; o[3] = (byte)' ';
        BitConverter.GetBytes(124).CopyTo(o, 4);
        BitConverter.GetBytes(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000).CopyTo(o, 8); // caps|height|width|pixelformat|linearsize
        BitConverter.GetBytes(h).CopyTo(o, 12);
        BitConverter.GetBytes(w).CopyTo(o, 16);
        BitConverter.GetBytes(pixelData.Length).CopyTo(o, 20);
        BitConverter.GetBytes(0).CopyTo(o, 24);
        BitConverter.GetBytes(1).CopyTo(o, 28);

        BitConverter.GetBytes(32).CopyTo(o, 76);          // pixelformat.size
        BitConverter.GetBytes(0x4).CopyTo(o, 80);         // DDPF_FOURCC
        if (dx10)
        {
            o[84] = (byte)'D'; o[85] = (byte)'X'; o[86] = (byte)'1'; o[87] = (byte)'0';
        }
        else
        {
            var ccBytes = Encoding.ASCII.GetBytes(fourCC);
            Array.Copy(ccBytes, 0, o, 84, Math.Min(4, ccBytes.Length));
        }
        BitConverter.GetBytes(0x1000).CopyTo(o, 108);     // caps.texture

        if (dx10)
        {
            BitConverter.GetBytes(dxgi).CopyTo(o, 128);
            BitConverter.GetBytes(3).CopyTo(o, 132);      // TEXTURE2D
            BitConverter.GetBytes(0).CopyTo(o, 136);
            BitConverter.GetBytes(1).CopyTo(o, 140);
            BitConverter.GetBytes(0).CopyTo(o, 144);
        }

        Buffer.BlockCopy(pixelData, 0, o, headerLen, pixelData.Length);
        return o;
    }
}

/// <summary>游戏目录定位与资源枚举。</summary>
public sealed class GameRoot
{
    public string Root { get; }

    public GameRoot(string root) => Root = root;

    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("DSR_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "DarkSoulsRemastered.exe"))) return env;

        var candidates = new[]
        {
            @"D:\GameVault\Platforms\steam\steamapps\common\DARK SOULS REMASTERED",
            @"C:\Program Files (x86)\Steam\steamapps\common\DARK SOULS REMASTERED",
            @"C:\Program Files\Steam\steamapps\common\DARK SOULS REMASTERED"
        };
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "DarkSoulsRemastered.exe"))) return c;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DarkSoulsRemastered.exe"))) return dir.FullName;
        return null;
    }

    public string Full(string rel) => Path.Combine(Root, rel);

    public IEnumerable<string> Files(string relDir, string pattern, bool recursive = false)
    {
        var dir = Full(relDir);
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, pattern,
                     recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            yield return f;
    }

    public IEnumerable<string> MsgBundles() => Files("msg", "*.msgbnd.dcx", true);
    public IEnumerable<string> TpfFiles() => Files("menu", "*.tpf.dcx", true)
        .Concat(Files("other", "*.tpf.dcx", true))
        .Concat(Files("map", "*.tpf.dcx", true));
    public IEnumerable<string> MsbFiles() => Files("map", "*.msb", true);
    public IEnumerable<string> EmevdFiles() => Files("event", "*.emevd.dcx", true).Concat(Files("map", "*.emevd.dcx", true));
    public IEnumerable<string> ChrbndFiles() => Files("chr", "*.chrbnd.dcx");
    public IEnumerable<string> ParamBundles() => Files("param", "*.parambnd.dcx", true);
}
