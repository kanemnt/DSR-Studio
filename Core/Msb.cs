using System;
using System.Collections.Generic;
using System.Text;

namespace DSRStudio.Core;

public sealed class MsbSection
{
    public string Name { get; set; } = "";
    public int NameOffset;
    public List<int> Records = new();
}

public sealed class MsbModel
{
    public int Index { get; set; }
    public int Offset { get; set; }
    public string Name { get; set; } = "";
    public string SibPath { get; set; } = "";
    public uint Type { get; set; }
}

/// <summary>
/// DSR 的 MapStudio/*.msb（本机实测形态）：
///   0x00 u32 版本(0) | 0x04 u32 首个段名偏移 | 0x08 u32 模型数 | 0x0C.. 指向各记录的 u32 表
///   段名：MODEL_PARAM_ST / EVENT_PARAM_ST / POINT_PARAM_ST / PARTS_PARAM_ST
///   每个段名前面紧邻它的「记录偏移表」，模型记录定长 92 字节：
///     +0x00 名字偏移(相对记录) | +0x0C 路径偏移 | +0x10 类型
/// </summary>
public sealed class MsbPoint
{
    public int Index { get; set; }
    public int Offset { get; set; }
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string TypeName { get; set; } = "";
}

public sealed class MsbPart
{
    public int Index { get; set; }
    public int Offset { get; set; }
    public string Name { get; set; } = "";
    public string ModelSib { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float ScaleX { get; set; }
    public float ScaleY { get; set; }
    public float ScaleZ { get; set; }
}

public sealed class MsbEvent
{
    public int Index { get; set; }
    public int Offset { get; set; }
    public string Name { get; set; } = "";
    public uint Field1 { get; set; }
    public uint Field2 { get; set; }
    public uint Seq { get; set; }
    public uint ParamOffset { get; set; }
}

public static class Msb
{
    static readonly string[] KnownSections =
        { "MODEL_PARAM_ST", "EVENT_PARAM_ST", "POINT_PARAM_ST", "PARTS_PARAM_ST" };

    public static List<MsbSection> Sections(byte[] d)
    {
        var list = new List<MsbSection>();
        foreach (var name in KnownSections)
        {
            var pat = Encoding.ASCII.GetBytes(name + "\0");
            int at = IndexOf(d, pat);
            if (at < 0) continue;

            var sec = new MsbSection { Name = name, NameOffset = at };
            var rev = new List<int>();
            int p = at - 4;
            // 有些段名前面有 0 填充，先跳过
            int guard = 0;
            while (p >= 4 && guard < 8 && BitConverter.ToInt32(d, p) == 0) { p -= 4; guard++; }

            int last = int.MaxValue;
            while (p >= 0)
            {
                int v = BitConverter.ToInt32(d, p);
                if (v <= at || v >= d.Length) break;
                if (v >= last) break;
                rev.Add(v);
                last = v;
                p -= 4;
            }
            rev.Reverse();
            sec.Records = rev;
            list.Add(sec);
        }
        return list;
    }

    public static List<MsbModel> Models(byte[] d, MsbSection sec)
    {
        var list = new List<MsbModel>();
        for (int i = 0; i < sec.Records.Count; i++)
        {
            int r = sec.Records[i];
            if (r + 92 > d.Length) break;

            var m = new MsbModel { Index = i, Offset = r };
            int nameOff = BitConverter.ToInt32(d, r);
            if (nameOff > 0 && r + nameOff < d.Length)
                m.Name = ReadAscii(d, r + nameOff, 40);

            int pathOff = BitConverter.ToInt32(d, r + 0x0C);
            if (pathOff > 0 && r + pathOff < d.Length)
                m.SibPath = ReadAscii(d, r + pathOff, 120);

            m.Type = BitConverter.ToUInt32(d, r + 0x10);
            list.Add(m);
        }
        return list;
    }

    public static List<MsbPoint> Points(byte[] d, MsbSection sec)
    {
        var list = new List<MsbPoint>();
        for (int i = 0; i < sec.Records.Count; i++)
        {
            int r = sec.Records[i];
            if (r + 0x40 > d.Length) continue;

            var pt = new MsbPoint { Index = i, Offset = r };
            int nameOff = BitConverter.ToInt32(d, r);
            if (nameOff > 0 && nameOff < 4096 && r + nameOff < d.Length)
                pt.Name = ReadAscii(d, r + nameOff, 64);

            pt.X = BitConverter.ToSingle(d, r + 0x10);
            pt.Y = BitConverter.ToSingle(d, r + 0x14);
            pt.Z = BitConverter.ToSingle(d, r + 0x18);

            int typeOff = BitConverter.ToInt32(d, r + 0x28);
            if (typeOff > 0 && typeOff < 4096 && r + typeOff < d.Length)
                pt.TypeName = ReadAscii(d, r + typeOff, 32);

            list.Add(pt);
        }
        return list;
    }

    public static List<MsbPart> Parts(byte[] d, MsbSection sec)
    {
        var list = new List<MsbPart>();
        for (int i = 0; i < sec.Records.Count; i++)
        {
            int r = sec.Records[i];
            if (r + 0x40 > d.Length) continue;

            var p = new MsbPart { Index = i, Offset = r };
            int nameOff = BitConverter.ToInt32(d, r);
            if (nameOff > 0 && nameOff < 4096 && r + nameOff < d.Length)
                p.Name = ReadAscii(d, r + nameOff, 64);

            int sibOff = BitConverter.ToInt32(d, r + 0x38);
            if (sibOff > 0 && sibOff < d.Length)
                p.ModelSib = ReadAscii(d, sibOff, 120);

            p.X = BitConverter.ToSingle(d, r + 0x14);
            p.Y = BitConverter.ToSingle(d, r + 0x18);
            p.Z = BitConverter.ToSingle(d, r + 0x1C);

            p.ScaleX = BitConverter.ToSingle(d, r + 0x2C);
            p.ScaleY = BitConverter.ToSingle(d, r + 0x30);
            p.ScaleZ = BitConverter.ToSingle(d, r + 0x34);

            list.Add(p);
        }
        return list;
    }

    public static List<MsbEvent> Events(byte[] d, MsbSection sec)
    {
        var list = new List<MsbEvent>();
        for (int i = 0; i < sec.Records.Count; i++)
        {
            int r = sec.Records[i];
            if (r + 0x20 > d.Length) continue;

            var e = new MsbEvent { Index = i, Offset = r };
            int nameOff = BitConverter.ToInt32(d, r);
            if (nameOff > 0 && nameOff < 4096 && r + nameOff < d.Length)
                e.Name = ReadAscii(d, r + nameOff, 64);
            e.Field1 = BitConverter.ToUInt32(d, r + 0x04);
            e.Field2 = BitConverter.ToUInt32(d, r + 0x08);
            e.Seq = BitConverter.ToUInt32(d, r + 0x0C);
            e.ParamOffset = BitConverter.ToUInt32(d, r + 0x10);
            list.Add(e);
        }
        return list;
    }

    /// <summary>原地写入 float（MSB 记录定长，不会改变文件结构）。</summary>
    public static void WriteFloat(byte[] d, int recordOffset, int fieldOffset, float value)
    {
        int p = recordOffset + fieldOffset;
        if (p < 0 || p + 4 > d.Length) return;
        BitConverter.GetBytes(value).CopyTo(d, p);
    }

    public static float ReadFloat(byte[] d, int recordOffset, int fieldOffset)
    {
        int p = recordOffset + fieldOffset;
        if (p < 0 || p + 4 > d.Length) return 0;
        return BitConverter.ToSingle(d, p);
    }

    public const int PartX = 0x14;
    public const int PartY = 0x18;
    public const int PartZ = 0x1C;

    public const int PointX = 0x10;
    public const int PointY = 0x14;
    public const int PointZ = 0x18;

    static readonly Encoding Sjis = InitSjis();

    static Encoding InitSjis()
    {
        try { return Encoding.GetEncoding(932); }
        catch { return Encoding.UTF8; }
    }

    /// <summary>MSB 閲岀殑鍚嶅瓧鏄?Shift-JIS锛坈p932锛夛紝涓嶆槸 UTF-8銆?/summary>
    static string ReadAscii(byte[] d, int off, int max)
    {
        if (off < 0 || off >= d.Length) return "";
        int end = off;
        int limit = Math.Min(d.Length, off + max);
        while (end < limit && d[end] != 0) end++;
        try { return Sjis.GetString(d, off, end - off); }
        catch { return Encoding.UTF8.GetString(d, off, end - off); }
    }

    static int IndexOf(byte[] h, byte[] n)
    {
        for (int i = 0; i + n.Length <= h.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < n.Length; k++) if (h[i + k] != n[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}

