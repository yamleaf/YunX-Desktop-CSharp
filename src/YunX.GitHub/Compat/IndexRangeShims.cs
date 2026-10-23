// =====================================================================================
//  C# 8 的索引与区间类型（System.Index / System.Range）
//
//  语法糖 `arr[^1]` / `str[1..3]` 在编译后需要 System.Index / System.Range
//  这两个**类型**存在（不是扩展方法能替代的）。net48 没有它们，
//  于是任何用到 `..` 或 `^` 的代码都报 CS0518「预定义类型 System.Range 未定义」。
//
//  这里在 System 命名空间下自己声明一份。注意：
//    · 必须在 System 命名空间里，因为编译器生成的是 `System.Index` 全限定引用
//    · 必须是 struct，且有编译器期望的构造函数签名
//    · 只补编译器真正用到的成员（FromStart/FromEnd/GetOffset/Start/End）
//
//  ★ 坑：名字不能改。叫 YunX.Compat.Index 编译器不认。
// =====================================================================================

#if NET48
using System.Runtime.CompilerServices;

namespace System
{
    /// <summary>C# 8 的 <c>^n</c> 索引。等价于 .NET Core 的 System.Index。</summary>
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "索引不能为负。");
            _value = fromEnd ? ~value : value;
        }

        private Index(int value) => _value = value;

        public static Index Start => new Index(0);
        public static Index End => new Index(~0);

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public static Index FromStart(int value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            return new Index(value);
        }

        public static Index FromEnd(int value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            return new Index(~value);
        }

        public int GetOffset(int length)
        {
            int offset = _value;
            if (IsFromEnd) offset += length + 1;
            return offset;
        }

        public override bool Equals(object? obj) => obj is Index other && Equals(other);

        public bool Equals(Index other) => _value == other._value;

        public override int GetHashCode() => _value;

        public override string ToString() => IsFromEnd ? "^" + Value.ToString() : Value.ToString();

        public static implicit operator Index(int value) => FromStart(value);
    }

    /// <summary>C# 8 的 <c>a..b</c> 区间。等价于 .NET Core 的 System.Range。</summary>
    internal readonly struct Range : IEquatable<Range>
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }
        public Index End { get; }

        public static Range All => new Range(Index.Start, Index.End);

        public static Range StartAt(Index start) => new Range(start, Index.End);

        public static Range EndAt(Index end) => new Range(Index.Start, end);

        public override bool Equals(object? obj) => obj is Range other && Equals(other);

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);

        public override int GetHashCode() => (Start.GetHashCode() << 1) ^ End.GetHashCode();

        public override string ToString() => Start + ".." + End;

        /// <summary>把区间换算成 (offset, length)；越界返回 (-1, 0)。</summary>
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
    }
}
#endif
