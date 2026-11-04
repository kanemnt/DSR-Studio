using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DSRStudio.Core;

public sealed class ParamField
{
    public string Name = "";
    public string Type = "";
    public int Offset;
    public int Size;
    public int BitOffset;
    public int BitCount;

    public bool IsBitfield => BitCount > 0;
    public string Label => IsBitfield ? $"{Name}:{BitCount}" : Name;
}

public sealed class ParamDef
{
    public string TableName = "";
    public List<ParamField> Fields = new();
    public int DeclaredStride;
    public bool Valid => Fields.Count > 0;
}

public sealed class ParamRow
{
    public uint Id;
    public int Offset;
}

public sealed class ParamTable
{
    public string Name = "";
    public int RowStride;
    public int RowOffsetBase;
    public List<ParamRow> Rows = new();
    public ParamDef? Def;
}

/// <summary>
/// DSR 的 PARAMDEF / PARAM 解析（本机实测布局）：
///   PARAMDEF 头 12 字节：u32 大小 | u16 头长(0x30) | u16 版本 | u16 字段数 | u16 记录步进(176)
///                        + 0x0C 起 32 字节表名
///   字段记录 176 字节：+0x40 类型(4) | +0x64 字节数 | +0x8C 内部名(32)
///   内部名后缀：":N" = 位域宽度，"[N]" = 数组（字节数已含在 size 内，不再相乘）
///   PARAM  头：u32 数据区 | u32 字符串区 | u16 | u16 行数 | 0x0C 表名(32)
///              + 0x30 起 12 字节行索引 {id, dataOff, nameOff}
/// </summary>
public static class Params
{
    public static Dictionary<string, ParamDef> LoadDefs(byte[] bundle)
    {
        var result = new Dictionary<string, ParamDef>(StringComparer.OrdinalIgnoreCase);
        var buf = Dcx.Decompress(bundle);

        foreach (var e in Bnd3.Entries(buf))
        {
            if (e.Size < 0x30 || e.Offset + e.Size > buf.Length) continue;

            int fieldCount = BitConverter.ToUInt16(buf, e.Offset + 0x08);
            int recSize = BitConverter.ToUInt16(buf, e.Offset + 0x0A);
            if (fieldCount <= 0 || fieldCount > 2000 || recSize < 64) continue;

            var def = new ParamDef { TableName = ReadAscii(buf, e.Offset + 0x0C, 32) };

            for (int k = 0; k < fieldCount; k++)
            {
                int r = e.Offset + 0x30 + k * recSize;
                if (r + recSize > buf.Length) break;

                string type = ReadAscii(buf, r + 0x40, 4);
                string raw = ReadAscii(buf, r + 0x8C, 32);
                int size = BitConverter.ToInt32(buf, r + 0x64);
                if (size <= 0 || size > 4096) size = SizeOf(type);

                var f = new ParamField { Type = type, Size = size };
                string name = raw;

                int colon = name.LastIndexOf(':');
                if (colon > 0 && int.TryParse(name[(colon + 1)..], out int bits) && bits >= 1 && bits <= 8)
                {
                    f.BitCount = bits;
                    name = name[..colon];
                }
                int bracket = name.IndexOf('[');
                if (bracket > 0) name = name[..bracket];

                f.Name = string.IsNullOrWhiteSpace(name) ? raw : name;
                def.Fields.Add(f);
            }

            Layout(def);
            if (!string.IsNullOrWhiteSpace(def.TableName)) result[def.TableName] = def;
        }
        return result;
    }

    /// <summary>按位打包计算字段偏移；末尾多出的字节从 pad 字段里扣掉。</summary>
    static void Layout(ParamDef def)
    {
        int off = 0, bit = 0;
        foreach (var f in def.Fields)
        {
            if (f.IsBitfield)
            {
                if (bit + f.BitCount > 8) { off++; bit = 0; }
                f.Offset = off;
                f.BitOffset = bit;
                bit += f.BitCount;
                if (bit == 8) { off++; bit = 0; }
            }
            else
            {
                if (bit > 0) { off++; bit = 0; }
                f.Offset = off;
                off += f.Size;
            }
        }
        if (bit > 0) off++;
        def.DeclaredStride = off;
    }

    /// <summary>用真实行宽校正尾部 pad 造成的对齐误差。</summary>
    public static void FitStride(ParamDef def, int stride)
    {
        if (stride <= 0 || def.DeclaredStride <= stride) return;
        int excess = def.DeclaredStride - stride;
        for (int i = def.Fields.Count - 1; i >= 0 && excess > 0; i--)
        {
            var f = def.Fields[i];
            if (f.IsBitfield) continue;
            if (f.Name.StartsWith("pad", StringComparison.OrdinalIgnoreCase))
            {
                int cut = Math.Min(excess, Math.Max(0, f.Size - 1));
                f.Size -= cut;
                excess -= cut;
            }
        }
    }

    public static Dictionary<string, ParamTable> LoadTables(byte[] bundle, Dictionary<string, ParamDef> defs)
    {
        var result = new Dictionary<string, ParamTable>(StringComparer.OrdinalIgnoreCase);
        var buf = Dcx.Decompress(bundle);

        foreach (var e in Bnd3.Entries(buf))
        {
            if (e.Size < 0x40 || e.Offset + e.Size > buf.Length) continue;

            int rowCount = BitConverter.ToUInt16(buf, e.Offset + 0x0A);
            if (rowCount <= 0 || rowCount > 200000) continue;

            string name = ReadAscii(buf, e.Offset + 0x0C, 32);
            var table = new ParamTable { Name = name, RowOffsetBase = e.Offset, Rows = new List<ParamRow>(rowCount) };

            if (defs.TryGetValue(name, out var def)) table.Def = def;
            else
            {
                table.Def = defs.FirstOrDefault(kv =>
                    name.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Value;
            }

            int idx = e.Offset + 0x30;
            for (int i = 0; i < rowCount; i++)
            {
                int p = idx + i * 12;
                if (p + 12 > buf.Length) break;
                uint id = BitConverter.ToUInt32(buf, p);
                int dataOff = BitConverter.ToInt32(buf, p + 4);
                table.Rows.Add(new ParamRow { Id = id, Offset = e.Offset + dataOff });
            }

            if (table.Rows.Count >= 2)
            {
                int stride = table.Rows[1].Offset - table.Rows[0].Offset;
                table.RowStride = stride > 0 ? stride : (table.Def?.DeclaredStride ?? 0);
            }
            else table.RowStride = table.Def?.DeclaredStride ?? 0;

            if (table.Def != null && table.RowStride > 0) FitStride(table.Def, table.RowStride);

            result[name] = table;
        }
        return result;
    }

    /// <summary>把文本写回缓冲区。位域用读-改-写，保证同字节其它位不受影响。</summary>
    public static bool TryWrite(byte[] buf, ParamRow row, ParamField f, string text, out string error)
    {
        error = "";
        int p = row.Offset + f.Offset;
        if (p < 0 || p + Math.Max(1, f.Size) > buf.Length) { error = "偏移越界"; return false; }

        var s = (text ?? "").Trim();
        if (s == "✓") s = "1";
        else if (s == "·" || s == "") s = "0";

        try
        {
            if (f.IsBitfield)
            {
                if (!int.TryParse(s, out int v)) { error = "需要整数"; return false; }
                int max = (1 << f.BitCount) - 1;
                if (v < 0 || v > max) { error = $"该位域范围 0..{max}"; return false; }
                int mask = max << f.BitOffset;
                buf[p] = (byte)((buf[p] & ~mask) | ((v << f.BitOffset) & mask));
                return true;
            }

            switch (f.Type.TrimEnd('\0'))
            {
                case "s8":
                    buf[p] = (byte)sbyte.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case "u8" or "b8":
                    buf[p] = byte.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case "s16":
                    BitConverter.GetBytes(short.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).CopyTo(buf, p);
                    return true;
                case "u16":
                    BitConverter.GetBytes(ushort.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).CopyTo(buf, p);
                    return true;
                case "s32":
                    BitConverter.GetBytes(int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).CopyTo(buf, p);
                    return true;
                case "u32":
                    BitConverter.GetBytes(uint.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).CopyTo(buf, p);
                    return true;
                case "f32":
                    BitConverter.GetBytes(float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).CopyTo(buf, p);
                    return true;
                case "f64":
                    BitConverter.GetBytes(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).CopyTo(buf, p);
                    return true;
                default:
                    if (f.Size == 4 && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out float fv))
                    {
                        BitConverter.GetBytes(fv).CopyTo(buf, p);
                        return true;
                    }
                    error = "不支持的字段类型 " + f.Type;
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = "格式无效：" + ex.GetType().Name;
            return false;
        }
    }

    public static string Format(byte[] buf, ParamRow row, ParamField f)
    {
        int p = row.Offset + f.Offset;
        if (p < 0 || p + Math.Max(1, f.Size) > buf.Length) return "";

        if (f.IsBitfield)
        {
            int raw = buf[p];
            int mask = ((1 << f.BitCount) - 1) << f.BitOffset;
            int v = (raw & mask) >> f.BitOffset;
            return f.BitCount == 1 ? (v != 0 ? "✓" : "·") : v.ToString();
        }

        try
        {
            return f.Type.TrimEnd('\0') switch
            {
                "s8" => ((sbyte)buf[p]).ToString(),
                "u8" or "b8" => buf[p].ToString(),
                "s16" => BitConverter.ToInt16(buf, p).ToString(),
                "u16" => BitConverter.ToUInt16(buf, p).ToString(),
                "s32" => BitConverter.ToInt32(buf, p).ToString(),
                "u32" => BitConverter.ToUInt32(buf, p).ToString(),
                "f32" => BitConverter.ToSingle(buf, p).ToString("0.###"),
                "f64" => BitConverter.ToDouble(buf, p).ToString("0.###"),
                _ => f.Size switch
                {
                    1 => buf[p].ToString(),
                    2 => BitConverter.ToUInt16(buf, p).ToString(),
                    4 => BitConverter.ToUInt32(buf, p).ToString(),
                    _ => ""
                }
            };
        }
        catch { return ""; }
    }

    static int SizeOf(string type) => type.TrimEnd('\0') switch
    {
        "s8" or "u8" or "b8" => 1,
        "s16" or "u16" => 2,
        "s32" or "u32" or "f32" => 4,
        "f64" => 8,
        _ => 4
    };

    static string ReadAscii(byte[] b, int off, int len)
    {
        if (off < 0 || off + len > b.Length) return "";
        int end = off;
        while (end < off + len && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, off, end - off).Trim();
    }
}
