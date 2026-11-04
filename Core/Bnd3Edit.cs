using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DSRStudio.Core;

/// <summary>
/// BND3 容器内的条目替换。
/// 做法：保留 0x00..数据区起点 的原样（头部 + 条目表 + 名字表），只重写条目表的
/// size / offset / size2 三个字段，然后把各条目的数据按原顺序重新拼接。
/// 这样不需要理解 BND3 头部那些未知字段，也避免搬动名字表。
/// </summary>
public static class Bnd3Edit
{
    public sealed class Result
    {
        public byte[] Data = Array.Empty<byte>();
        public string Error = "";
        public long OldSize;
        public long NewSize;
        public bool Ok => Error.Length == 0;
    }

    public static Result Replace(byte[] bnd, string entryName, byte[] newBlob)
    {
        var res = new Result();
        var entries = Bnd3.Entries(bnd);
        if (entries.Count == 0) { res.Error = "容器内没有条目"; return res; }

        int target = -1;
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Name, entryName, StringComparison.OrdinalIgnoreCase)) { target = i; break; }
        if (target < 0) { res.Error = "找不到条目：" + entryName; return res; }

        // 数据区起点 = 所有条目里最小的偏移
        int dataStart = int.MaxValue;
        foreach (var e in entries) dataStart = Math.Min(dataStart, e.Offset);
        if (dataStart < 0x20 || dataStart > bnd.Length) { res.Error = "数据区起点异常"; return res; }

        // 按原顺序拼接数据
        var blobs = new List<byte[]>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == target) { blobs.Add(newBlob); continue; }
            var b = new byte[entries[i].Size];
            Buffer.BlockCopy(bnd, entries[i].Offset, b, 0, entries[i].Size);
            blobs.Add(b);
        }

        int total = dataStart;
        foreach (var b in blobs) total += b.Length;

        var outBuf = new byte[total];
        Buffer.BlockCopy(bnd, 0, outBuf, 0, dataStart);

        int cursor = dataStart;
        for (int i = 0; i < entries.Count; i++)
        {
            Buffer.BlockCopy(blobs[i], 0, outBuf, cursor, blobs[i].Length);

            int rec = 0x20 + i * 24;
            BitConverter.GetBytes(blobs[i].Length).CopyTo(outBuf, rec + 4);   // size
            BitConverter.GetBytes(cursor).CopyTo(outBuf, rec + 8);            // offset
            BitConverter.GetBytes(blobs[i].Length).CopyTo(outBuf, rec + 20);  // size2
            cursor += blobs[i].Length;
        }

        res.Data = outBuf;
        res.OldSize = bnd.Length;
        res.NewSize = outBuf.Length;
        return res;
    }

    /// <summary>回包后自检：重新解析，逐条比对内容。</summary>
    public static string Verify(byte[] rebuilt, string entryName, byte[] expected)
    {
        var entries = Bnd3.Entries(rebuilt);
        int idx = -1;
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Name, entryName, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx < 0) return "回包后找不到条目";

        var e = entries[idx];
        if (e.Size != expected.Length) return $"条目大小不符（{e.Size} != {expected.Length}）";
        for (int i = 0; i < e.Size; i++)
            if (rebuilt[e.Offset + i] != expected[i]) return "条目内容不一致";
        return "";
    }
}