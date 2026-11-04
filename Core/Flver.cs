using System.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace DSRStudio.Core;

public sealed class BundleEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Size { get; set; }
    public int Offset { get; set; }
    public string Detail { get; set; } = "";
    public string Header { get; set; } = "";
}

/// <summary>
/// 模型容器（*.chrbnd / *.partsbnd / *.objbnd）的最小只读视图：
/// 列出 BND 内的条目；对 .flver 顺带读出魔数、版本、文件大小与少量头部计数。
/// FLVER 的完整结构（骨骼 / 材质 / 网格 / 顶点声明）尚未解析。
/// </summary>
public static class Flver
{
    public static List<BundleEntry> Scan(byte[] bndData)
    {
        var list = new List<BundleEntry>();
        var buf = Dcx.Decompress(bndData);

        foreach (var e in Bnd3.Entries(buf))
        {
            var item = new BundleEntry
            {
                Index = e.Index,
                Name = e.Name,
                Size = e.Size,
                Offset = e.Offset,
                Kind = KindOf(e.Name)
            };

            if (e.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase) && e.Offset + 0x20 <= buf.Length)
            {
                var span = buf.AsSpan(e.Offset, Math.Min(e.Size, 0x40));
                bool magicOk = span.Length >= 6 && span[0] == (byte)'F' && span[1] == (byte)'L'
                               && span[2] == (byte)'V' && span[3] == (byte)'E' && span[4] == (byte)'R';
                if (magicOk)
                {
                    uint version = BitConverter.ToUInt32(buf, e.Offset + 0x08);
                    uint fileSize = BitConverter.ToUInt32(buf, e.Offset + 0x0C);
                    uint dummyCount = BitConverter.ToUInt32(buf, e.Offset + 0x14);
                    uint boneCount = BitConverter.ToUInt32(buf, e.Offset + 0x1C);
                    item.Detail = $"FLVER v{version:X8} · 声明大小 {fileSize} · dummy {dummyCount} · bone {boneCount}";

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"魔数 FLVER · 版本 0x{version:X8} · 头部 0x0C = {fileSize}（容器内实际 {e.Size}；该字段并非总是文件大小）");
                    sb.AppendLine();
                    sb.AppendLine("头部偏移   值(u32)      十六进制");
                    for (int i = 0; i < 0x60 && e.Offset + i + 4 <= buf.Length; i += 4)
                    {
                        uint v = BitConverter.ToUInt32(buf, e.Offset + i);
                        sb.AppendLine($"  +0x{i:X2}   {v,-12} 0x{v:X8}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("包围盒（0x28 起 6 个 float：min xyz / max xyz）");
                    for (int i = 0; i < 6 && e.Offset + 0x28 + i * 4 + 4 <= buf.Length; i++)
                    {
                        float b = BitConverter.ToSingle(buf, e.Offset + 0x28 + i * 4);
                        sb.AppendLine($"  {i}: {b:0.####}");
                    }
                    var bones = FindBones(buf, e.Offset, e.Size, (int)boneCount);
                    sb.AppendLine();
                    if (bones.Count > 0)
                    {
                        sb.AppendLine($"骨骼表（自校验命中：{bones.Count} 条，与头部声明一致）");
                        foreach (var b in bones)
                            sb.AppendLine($"  [{b.Index}] +0x{b.Offset:X4}  {b.Name}   ({b.Note})");
                    }
                    else sb.AppendLine("骨骼表：未找到能通过自校验的步长（不在界面上猜）");

                    var names = Utf16Names(buf, e.Offset, e.Size, 64);
                    sb.AppendLine();
                    sb.AppendLine("材质 / 贴图信息（UTF-16 提取）");

                    void Section(string title, Func<string, bool> match)
                    {
                        var hit = names.Where(x => match(x.Item2)).ToList();
                        if (hit.Count == 0) return;
                        sb.AppendLine("  " + title);
                        foreach (var s in hit.Take(12))
                            sb.AppendLine($"    0x{s.Item1:X4}  {s.Item2}");
                    }

                    Section("材质名", s => s.StartsWith("Material", StringComparison.OrdinalIgnoreCase));
                    Section("MTD（材质定义）", s => s.EndsWith(".mtd", StringComparison.OrdinalIgnoreCase));
                    Section("贴图槽", s => s.StartsWith("g_", StringComparison.Ordinal));
                    Section("贴图文件", s => s.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                                          || s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                          || s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                    Section("其它字符串", s => !s.StartsWith("Material", StringComparison.OrdinalIgnoreCase)
                                          && !s.StartsWith("g_", StringComparison.Ordinal)
                                          && !s.EndsWith(".mtd", StringComparison.OrdinalIgnoreCase)
                                          && !s.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                                          && !s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                          && !s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));

                    item.Header = sb.ToString();
                }
                else item.Detail = "FLVER 魔数不符";
            }

            list.Add(item);
        }
        return list;
    }

    /// <summary>FLVER 里的名字是 UTF-16LE（不是 ASCII），这里按 UTF-16 扫描候选名字。</summary>
    static List<(int, string)> Utf16Names(byte[] buf, int baseOff, int size, int max)
    {
        var list = new List<(int, string)>();
        int end = Math.Min(buf.Length - 1, baseOff + size);
        int i = baseOff + (baseOff & 1);
        while (i + 4 <= end && list.Count < max)
        {
            // 需要 UTF-16 可打印字符（含中日文）且以 0x0000 结尾
            int p = i;
            var sb = new StringBuilder();
            while (p + 1 < end)
            {
                char c = (char)(buf[p] | (buf[p + 1] << 8));
                if (c == 0) break;
                if (c < 0x20 || c > 0x7E) { sb.Clear(); break; }
                sb.Append(c);
                p += 2;
            }
            if (sb.Length >= 2 && p + 1 < end && buf[p] == 0 && buf[p + 1] == 0)
            {
                list.Add((i - baseOff, sb.ToString()));
                i = p + 2;
            }
            else i += 2;
        }
        return list;
    }

    public sealed class FlverBone
    {
        public int Index { get; set; }
        public int Offset { get; set; }
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// 用「N 条记录 × 固定步长，每条 +0x00 是一个能解出合法 UTF-16 名字的偏移」做自校验，
    /// 自动定位骨骼表；找不到匹配步长就返回空（宁可不显示，也不猜）。
    /// </summary>
    public static List<FlverBone> FindBones(byte[] buf, int baseOff, int size, int boneCount)
    {
        var result = new List<FlverBone>();
        if (boneCount <= 0 || boneCount > 512) return result;

        int[] strides = { 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0 };
        foreach (var stride in strides)
        {
            for (int start = 0x60; start + stride * boneCount <= size; start += 4)
            {
                var names = new List<(int, string)>();
                bool ok = true;
                for (int k = 0; k < boneCount; k++)
                {
                    int r = baseOff + start + k * stride;
                    if (r + 4 > buf.Length) { ok = false; break; }
                    uint off = BitConverter.ToUInt32(buf, r);
                    var s = ReadUtf16At(buf, baseOff + (int)off, baseOff + size);
                    if (s == null) { ok = false; break; }
                    names.Add((start + k * stride, s));
                }
                if (!ok) continue;

                // 名字数量与声明一致、且名字互不相同才算命中
                if (names.Select(x => x.Item2).Distinct().Count() != boneCount) continue;

                for (int k = 0; k < names.Count; k++)
                    result.Add(new FlverBone
                    {
                        Index = k, Offset = names[k].Item1, Name = names[k].Item2,
                        Note = "步长 0x" + stride.ToString("X")
                    });
                return result;
            }
        }
        return result;
    }

    static string? ReadUtf16At(byte[] buf, int p, int limit)
    {
        if (p < 0 || p + 4 > limit || p + 4 > buf.Length) return null;
        var sb = new StringBuilder();
        int i = p;
        while (i + 1 < buf.Length && i < limit)
        {
            char c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c == 0) break;
            if (c < 0x20 || c > 0x7E) return null;
            sb.Append(c);
            i += 2;
        }
        if (sb.Length < 2 || sb.Length > 32) return null;
        return sb.ToString();
    }

    static string KindOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>把 FLVER 的引用关系画成缩进树（材质 → MTD → 贴图槽/贴图文件）。</summary>
    public static string Tree(byte[] container, BundleEntry e)
    {
        var buf = Dcx.Decompress(container);
        var names = Utf16Names(buf, e.Offset, e.Size, 64).Select(x => x.Item2).ToList();

        string Pick(Func<string, bool> f) => names.FirstOrDefault(f) ?? "";
        var slots = names.Where(n => n.StartsWith("g_", StringComparison.Ordinal)).ToList();
        var texs = names.Where(n => n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                                 || n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                 || n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList();
        var mats = names.Where(n => n.StartsWith("Material", StringComparison.OrdinalIgnoreCase)).ToList();
        var mtd = Pick(n => n.EndsWith(".mtd", StringComparison.OrdinalIgnoreCase));

        uint version = e.Offset + 0x0C <= buf.Length ? BitConverter.ToUInt32(buf, e.Offset + 0x08) : 0;
        uint bones = e.Offset + 0x20 <= buf.Length ? BitConverter.ToUInt32(buf, e.Offset + 0x1C) : 0;
        var boneList = FindBones(buf, e.Offset, e.Size, (int)bones);

        var sb = new StringBuilder();
        sb.AppendLine(e.Name);
        sb.AppendLine($"├─ FLVER 0x{version:X8} · {e.Size:N0} 字节");
        sb.AppendLine($"├─ 骨骼 {boneList.Count}" + (boneList.Count > 0 ? "：" + string.Join(", ", boneList.Select(b => b.Name)) : "（未解出）"));

        if (mats.Count == 0 && mtd.Length == 0)
        {
            sb.AppendLine("└─ 材质：未在文件内找到名称");
            return sb.ToString();
        }

        for (int i = 0; i < Math.Max(1, mats.Count); i++)
        {
            bool lastMat = i == Math.Max(1, mats.Count) - 1;
            sb.AppendLine((lastMat ? "└─ " : "├─ ") + "材质 " + (mats.Count > 0 ? mats[i] : "（未命名）"));
            var pad = lastMat ? "   " : "│  ";
            if (mtd.Length > 0) sb.AppendLine(pad + "├─ MTD  " + mtd);

            var slotU = slots.Distinct().ToList();
            var texU = texs.Distinct().ToList();
            if (slotU.Count > 0) sb.AppendLine(pad + "├─ 贴图槽  " + string.Join(", ", slotU));
            sb.AppendLine(pad + "└─ 贴图文件  " + (texU.Count > 0 ? string.Join(", ", texU) : "（无）"));
            if (mats.Count > 1) break;   // 多材质时只用第一组槽位，避免重复
        }
        return sb.ToString();
    }
}
