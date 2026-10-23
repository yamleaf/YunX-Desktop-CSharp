// =====================================================================================
//  C# 9 / C# 11 语言特性所需的「编译器垫片」
//
//  LangVersion=12 时编译器会生成对这些类型的引用，但它们是 .NET 5+ 才有的 BCL 类型，
//  net48 上没有 → 报「找不到类型或命名空间名 IsExternalInit」之类。
//
//  做法是自己在当前程序集里声明一份同名同命名空间的类型：编译器一看到就满意了。
//  这些类型只服务于编译器，运行时没有任何逻辑。
//
//    · IsExternalInit                 → init 访问器
//    · RequiredMemberAttribute        → required 修饰符
//    · CompilerFeatureRequiredAttribute
//    · SetsRequiredMembersAttribute
//    · System.Runtime.CompilerServices.RefSafetyRulesAttribute → ref struct 返回值安全规则
// =====================================================================================

#if NET48
using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    /// <summary>让 <c>init</c> 访问器可编译（C# 9）。</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class IsExternalInit
    {
    }

    /// <summary>让 <c>required</c> 修饰符可编译（C# 11）。</summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequiredMemberAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }

    /// <summary>让标记了 <c>required</c> 成员的构造函数可以跳过成员初始化检查。</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SetsRequiredMembersAttribute : Attribute
    {
    }

    /// <summary>
    /// C# 11 的 <c>ref</c> 字段 / <c>scoped</c> 相关的安全规则标记。
    /// 编译器在 net5+ 上会往程序集里写这个特性；net48 缺它时只是警告，
    /// 但既然声明成本极低，就一并补上，免得 CI 里出现噪声。
    /// </summary>
    [AttributeUsage(AttributeTargets.Module, Inherited = false)]
    public sealed class RefSafetyRulesAttribute : Attribute
    {
        public RefSafetyRulesAttribute(int version) => Version = version;

        public int Version { get; }
    }
}
#endif
