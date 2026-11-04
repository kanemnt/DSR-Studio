using System;
using System.Collections.Generic;
using System.Text;

namespace DSRStudio.Core;

public sealed class EmevdEvent
{
    public int Index { get; set; }
    public uint Id { get; set; }
    public uint InstrCount { get; set; }
    public uint InstrOffset { get; set; }
    public int RestBehavior { get; set; }
    public string Note { get; set; } = "";
}

public sealed class EmevdFile
{
    public string Name { get; set; } = "";
    public uint EventCount { get; set; }
    public int FileSize { get; set; }
    public List<EmevdEvent> Events { get; set; } = new();
    public string Warning { get; set; } = "";
    public byte[] Raw { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// DSR 的 EMEVD（事件脚本）。本机实测布局：
///   0x00  "EVD\0"
///   0x08  u32 事件数
///   0x0C  u32 文件大小
///   0x70  事件表起点，每 28 字节：
///         +0x00 事件 ID | +0x04 指令数 | +0x08 指令区偏移 |
///         +0x0C 0 | +0x10 重启类型/图层(-1) | +0x14/0x18 保留
///   指令区从第一个事件的指令偏移开始（通常 0x1800 附近）。
/// </summary>
public static class Emevd
{
    public static EmevdFile Parse(byte[] raw)
    {
        var file = new EmevdFile();
        var d = Dcx.Decompress(raw);
        file.Raw = d;
        file.FileSize = d.Length;

        if (d.Length < 0x80 || d[0] != (byte)'E' || d[1] != (byte)'V' || d[2] != (byte)'D' || d[3] != 0)
        {
            file.Warning = "不是 EVD 容器（魔数不符）";
            return file;
        }

        uint count = BitConverter.ToUInt32(d, 0x08);
        file.EventCount = count;
        if (count == 0 || count > 100000) { file.Warning = "事件数异常：" + count; return file; }

        const int start = 0x70;
        const int stride = 28;
        if (start + count * stride > d.Length)
        {
            file.Warning = "事件表超出文件范围";
            return file;
        }

        for (int i = 0; i < count; i++)
        {
            int p = start + i * stride;
            var e = new EmevdEvent
            {
                Index = i,
                Id = BitConverter.ToUInt32(d, p),
                InstrCount = BitConverter.ToUInt32(d, p + 4),
                InstrOffset = BitConverter.ToUInt32(d, p + 8),
                RestBehavior = BitConverter.ToInt32(d, p + 0x10)
            };
            e.Note = e.InstrOffset >= d.Length ? "指令偏移越界" : "";
            file.Events.Add(e);
        }
        return file;
    }
}

public sealed class EmevdInstr
{
    public int Index { get; set; }
    public uint Type { get; set; }
    public uint Sub { get; set; }
    public uint ArgBytes { get; set; }
    public uint ArgOffset { get; set; }
    public long Flag { get; set; }
    public string Args { get; set; } = "";
    public string Name { get; set; } = "";
}

public static class EmevdInstrs
{
    public const int Size = 24;

    /// <summary>
    /// 指令记录 24 字节（实测 100% 对得上）：区段长度 == 指令数 × 24
    ///   +0x00 恒 0 | +0x04 类型 | +0x08 子值(0..3) | +0x0C 参数字节数 |
    ///   +0x10 参数池偏移（同一事件内按 20 递增）| +0x14 标志（多为 -1）
    /// 参数池位于指令区之前，按 ArgOffset 取值、长度 ArgBytes。
    /// </summary>
    public static List<EmevdInstr> Read(byte[] d, EmevdEvent e)
    {
        var list = new List<EmevdInstr>();
        long start = e.InstrOffset;
        if (start < 0 || start >= d.Length) return list;

        for (int k = 0; k < e.InstrCount; k++)
        {
            long p = start + k * Size;
            if (p + Size > d.Length) break;

            var ins = new EmevdInstr
            {
                Index = k,
                Type = BitConverter.ToUInt32(d, (int)p + 0x04),
                Sub = BitConverter.ToUInt32(d, (int)p + 0x08),
                ArgBytes = BitConverter.ToUInt32(d, (int)p + 0x0C),
                ArgOffset = BitConverter.ToUInt32(d, (int)p + 0x10),
                Flag = BitConverter.ToInt32(d, (int)p + 0x14)
            };
            int key = (int)(ins.Type << 8) | (int)ins.Sub;
            if (EvdNames.Map.TryGetValue(key, out var nm)) ins.Name = nm;

            if (ins.ArgOffset > 0 && ins.ArgOffset + ins.ArgBytes <= d.Length && ins.ArgBytes <= 256)
            {
                var parts = new List<string>();

                // 优先按 EMEDF 的参数类型与名称展开
                if (EvdDefs.Map.TryGetValue(key, out var defs) && defs.Count > 0)
                {
                    long a = ins.ArgOffset;
                    long limit = ins.ArgOffset + ins.ArgBytes;
                    foreach (var prm in defs)
                    {
                        if (a + prm.Size > limit) break;
                        long v = prm.Size switch
                        {
                            1 => d[(int)a],
                            2 => BitConverter.ToInt16(d, (int)a),
                            _ => BitConverter.ToInt32(d, (int)a)
                        };
                        var label = prm.Name.Length > 0 ? prm.Name + "=" : "";
                        var en = prm.Enum.Length > 0 ? " (" + prm.Enum + ")" : "";
                        parts.Add(label + v + en);
                        a += prm.Size;
                    }
                }

                if (parts.Count == 0)
                    for (int a = 0; a + 4 <= ins.ArgBytes; a += 4)
                        parts.Add(BitConverter.ToInt32(d, (int)(ins.ArgOffset + a)).ToString());

                ins.Args = string.Join(", ", parts);

                if (parts.Count == 0)
                {
                    var hex = new System.Text.StringBuilder();
                    for (int a = 0; a < ins.ArgBytes; a++) hex.Append($"{d[ins.ArgOffset + a]:X2} ");
                    ins.Args = hex.ToString().Trim();
                }
            }
            list.Add(ins);
        }
        return list;
    }
}
