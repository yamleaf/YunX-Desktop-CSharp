// =====================================================================================
//  缺失的字符串 / 集合扩展方法
//
//  两个框架都编译这个文件：
//    · net8：实例方法 / BCL 重载优先，这里的扩展永远不会被选中 → 零影响
//    · net48：net48 缺少这些重载，扩展方法被选中 → 补齐
//
//  ★ 因为扩展方法的查找优先级低于实例方法，这种「两框架都定义」的写法
//    天然安全，不会产生二义性。但要注意：**不要**定义 net48 上已存在的
//    扩展 —— 例如 ToHashSet 在 .NET Framework 4.7.2+ 的 Linq 里已经有了，
//    再定义会 CS0121 二义性（这个坑踩过一次）。
// =====================================================================================

using System;

namespace YunX.Core.Compat
{
    internal static class StringCompatExtensions
    {
        /// <summary>等价于 net8 的 <c>string.Contains(string, StringComparison)</c>。</summary>
        public static bool Contains(this string source, string value, StringComparison comparison)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
#if NET48
            return source.IndexOf(value, comparison) >= 0;
#else
            return source.Contains(value, comparison);
#endif
        }

        /// <summary>等价于 net8 的 <c>string.Contains(char)</c>。</summary>
        public static bool Contains(this string source, char value)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
#if NET48
            return source.IndexOf(value) >= 0;
#else
            return source.Contains(value);
#endif
        }

        /// <summary>等价于 net8 的 <c>string.StartsWith(char)</c>。</summary>
        public static bool StartsWith(this string source, char value)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
#if NET48
            return source.Length > 0 && source[0] == value;
#else
            return source.StartsWith(value);
#endif
        }

        /// <summary>等价于 net8 的 <c>string.EndsWith(char)</c>。</summary>
        public static bool EndsWith(this string source, char value)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
#if NET48
            return source.Length > 0 && source[source.Length - 1] == value;
#else
            return source.EndsWith(value);
#endif
        }

        /// <summary>等价于 net8 的 <c>string.Split(char, StringSplitOptions)</c>。</summary>
        public static string[] Split(this string source, char separator, StringSplitOptions options)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
#if NET48
            // net48 只有 Split(char[]) / Split(char[], StringSplitOptions)，没有单 char 重载
            return source.Split(new[] { separator }, options);
#else
            return source.Split(separator, options);
#endif
        }

        /// <summary>等价于 net8 的 <c>string.Split(char, int)</c>（最多 count 段）。</summary>
        public static string[] Split(this string source, char separator, int count,
            StringSplitOptions options = StringSplitOptions.None)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
#if NET48
            return source.Split(new[] { separator }, count, options);
#else
            return source.Split(separator, count, options);
#endif
        }
    }
}
