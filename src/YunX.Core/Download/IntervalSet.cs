namespace YunX.Core.Download;

/// <summary>
/// 已完成字节区间的集合（有序、自动合并）。
///
/// 断点续传需要精确知道"哪些字节已经落盘"。因为分片是并发完成的（乱序），
/// 不能只记一个"已下载长度"，必须记录区间列表，否则重启后会把已完成的部分重下。
/// </summary>
public sealed class IntervalSet
{
    private readonly List<(long Start, long End)> _items = new();
    private readonly object _gate = new();

    public long TotalBytes { get; private set; }

    public void Add(long start, long end)
    {
        if (end < start) return;
        lock (_gate)
        {
            var list = _items;
            int i = 0;

            // 跳过完全在新区间之前、且不相邻的
            while (i < list.Count && list[i].End + 1 < start) i++;

            long newStart = start, newEnd = end;
            int removeFrom = i, removeCount = 0;

            while (i < list.Count && list[i].Start <= newEnd + 1)
            {
                newStart = Math.Min(newStart, list[i].Start);
                newEnd = Math.Max(newEnd, list[i].End);
                removeCount++;
                i++;
            }

            long before = removeCount > 0
                ? list.GetRange(removeFrom, removeCount).Sum(x => x.End - x.Start + 1)
                : 0;

            if (removeCount > 0) list.RemoveRange(removeFrom, removeCount);
            list.Insert(removeFrom, (newStart, newEnd));

            TotalBytes += (newEnd - newStart + 1) - before;
        }
    }

    public bool Contains(long pos)
    {
        lock (_gate)
        {
            foreach (var (s, e) in _items)
            {
                if (pos < s) return false;
                if (pos <= e) return true;
            }
            return false;
        }
    }

    /// <summary>若 <paramref name="pos"/> 落在某个已完成区间内，返回该区间的结束位置；否则返回 -1。</summary>
    public long EndOfContaining(long pos)
    {
        lock (_gate)
        {
            foreach (var (s, e) in _items)
            {
                if (pos < s) return -1;
                if (pos <= e) return e;
            }
            return -1;
        }
    }

    public List<(long Start, long End)> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    public static IntervalSet FromPairs(IEnumerable<(long Start, long End)> pairs)
    {
        var set = new IntervalSet();
        foreach (var (s, e) in pairs) set.Add(s, e);
        return set;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            TotalBytes = 0;
        }
    }
}
