using System;
using System.Collections.Generic;
using System.Text;

namespace DSRStudio.Core;


public sealed class TaeAnim
{
    public int Index { get; set; }
    public uint AnimId { get; set; }
    public uint EventTableOffset { get; set; }
    public uint EventCount { get; set; }
    public uint EventArrayOffset { get; set; }
}
public sealed class TaeFile
{
    public int Size { get; set; }
    public string Magic { get; set; } = "";
    public List<(string Key, uint Value)> Header { get; set; } = new();
    public List<string> Names { get; set; } = new();
    public string Hex { get; set; } = "";
    public string Note { get; set; } = "";
    public List<TaeAnim> Anims { get; set; } = new();
}

/// <summary>
/// TAE（动画事件表）。本机实测（c1000.tae，500 字节）：
///   0x00 "TAE " | 0x08 版本 | 0x0C 文件大小 | 0x10 / 0x18 / 0x1C 段偏移
///   内部含 UTF-16 动画名（skeleton.hkx / c1000.sib / a00_0000.hkx）
/// 事件级结构（动画 → 事件 → 起止帧）尚未解析，这里只给清单与结构。
/// </summary>
public static class Tae
{
    public static TaeFile Parse(byte[] raw)
    {
        var f = new TaeFile();
        // 顶层是 BND，先解包找 .tae
        byte[] d = Dcx.Decompress(raw);
        byte[]? tae = null;
        string taeName = "";

        if (d.Length > 4 && d[0] == (byte)'T' && d[1] == (byte)'A' && d[2] == (byte)'E')
        {
            tae = d;
            taeName = "TAE";
        }
        else
        {
            foreach (var e in Bnd3.Entries(d))
            {
                if (!e.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)) continue;
                tae = new byte[e.Size];
                Buffer.BlockCopy(d, e.Offset, tae, 0, e.Size);
                taeName = e.Name;
                break;
            }
        }

        if (tae == null)
        {
            f.Note = "容器内没有 .tae";
            return f;
        }

        f.Size = tae.Length;
        f.Magic = Encoding.ASCII.GetString(tae, 0, Math.Min(4, tae.Length)).TrimEnd('\0');
        f.Note = taeName;

        string[] keys = { "0x04", "版本", "文件大小", "段A偏移", "动画数", "段B偏移", "段C偏移", "标志" };
        for (int i = 0; i < keys.Length && 0x04 + i * 4 + 4 <= tae.Length; i++)
            f.Header.Add((keys[i], BitConverter.ToUInt32(tae, 0x04 + i * 4)));

        // UTF-16 名称（动画 / 骨架）
        var names = new List<string>();
        int p = 0;
        while (p + 6 < tae.Length)
        {
            var sb = new StringBuilder();
            int q = p;
            while (q + 1 < tae.Length)
            {
                char c = (char)(tae[q] | (tae[q + 1] << 8));
                if (c == 0) break;
                if (c < 0x20 || c > 0x7E) { sb.Clear(); break; }
                sb.Append(c);
                q += 2;
            }
            if (sb.Length >= 3 && q + 1 < tae.Length && tae[q] == 0 && tae[q + 1] == 0
                && (sb.ToString().Contains('.') || sb.ToString().Contains('_')))
            {
                var s = sb.ToString();
                if (!names.Contains(s)) names.Add(s);
                p = q + 2;
            }
            else p += 2;
        }
        f.Names = names;

        f.Anims = Animations(tae);

        var hex = new StringBuilder();
        for (int i = 0; i < tae.Length && i < 4096; i += 16)
        {
            hex.Append($"{i:X4}  ");
            for (int k = 0; k < 16 && i + k < tae.Length; k++) hex.Append($"{tae[i + k]:X2} ");
            hex.Append('\n');
        }
        f.Hex = hex.ToString();
        return f;
    }

    /// <summary>
    /// 动画清单（已验证）：0x54 动画数，0x58 动画表，每条 8 字节 {动画ID, 事件表偏移}；
    /// 事件表头 {事件数, 事件数组偏移}。事件记录本身是变长指针结构，未解码。
    /// </summary>
    public static List<TaeAnim> Animations(byte[] tae)
    {
        var list = new List<TaeAnim>();
        if (tae == null || tae.Length < 0x60) return list;

        uint animCount = BitConverter.ToUInt32(tae, 0x54);
        uint animTable = BitConverter.ToUInt32(tae, 0x58);
        if (animCount == 0 || animCount > 10000 || animTable + animCount * 8 > tae.Length) return list;

        for (int i = 0; i < animCount; i++)
        {
            int p = (int)animTable + i * 8;
            uint id = BitConverter.ToUInt32(tae, p);
            uint etOff = BitConverter.ToUInt32(tae, p + 4);

            uint ec = 0, eaOff = 0;
            if (etOff > 0 && etOff + 8 <= tae.Length)
            {
                ec = BitConverter.ToUInt32(tae, (int)etOff);
                eaOff = BitConverter.ToUInt32(tae, (int)etOff + 4);
            }
            list.Add(new TaeAnim
            {
                Index = i,
                AnimId = id,
                EventTableOffset = etOff,
                EventCount = ec,
                EventArrayOffset = eaOff
            });
        }
        return list;
    }

}
