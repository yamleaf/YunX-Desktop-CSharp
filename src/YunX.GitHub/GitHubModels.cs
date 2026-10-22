using YunX.Core.Mirrors;

namespace YunX.GitHub;

/// <summary>链接指向的目标类型。</summary>
public enum TargetKind
{
    /// <summary>整个仓库 → 源码 zip。</summary>
    Repo,
    /// <summary>目录 → 打包整仓 zip（GitHub 不提供单目录包）。</summary>
    Tree,
    /// <summary>单个文件 → raw。</summary>
    File,
    /// <summary>Release 页面 → 资产列表。</summary>
    Releases,
    /// <summary>已是归档直链。</summary>
    Archive,
    /// <summary>Release 资产直链。</summary>
    Asset,
    /// <summary>Gist。</summary>
    Gist,
}

/// <summary>解析结果。</summary>
public sealed class GitHubTarget
{
    public TargetKind Kind { get; init; }
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    public string? Ref { get; init; }
    public string? Path { get; init; }
    public string? Tag { get; init; }
    public string? AssetName { get; init; }
    /// <summary>可直接下载的 URL；null 表示需要先调 API 查询。</summary>
    public string? Url { get; init; }
    public string? SuggestedName { get; init; }
    /// <summary>给用户看的补充说明。</summary>
    public string Note { get; init; } = "";
    public string RawInput { get; init; } = "";

    public string FullName => string.IsNullOrEmpty(Repo) ? Owner : $"{Owner}/{Repo}";

    public string KindLabel => Kind switch
    {
        TargetKind.Repo => "仓库源码包",
        TargetKind.Tree => "目录（将下载整仓压缩包）",
        TargetKind.File => "单个文件",
        TargetKind.Releases => "Release 资产",
        TargetKind.Archive => "归档压缩包",
        TargetKind.Asset => "Release 资产文件",
        TargetKind.Gist => "Gist 代码片段",
        _ => Kind.ToString(),
    };
}

/// <summary>一个 Release 资产。</summary>
public sealed class ReleaseAsset
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Url { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>解析后的可下载项。</summary>
public sealed class ResolvedContent
{
    public TargetKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    /// <summary>(Url, FileName, Size)。Size 为 0 表示未知。</summary>
    public List<(string Url, string FileName, long Size)> Items { get; init; } = new();

    /// <summary>
    /// 来源仓库（仅 GitHub 来源有值）。
    ///
    /// <para>UI 用它在解析结果里提供「浏览仓库文件树」入口 —— 没有它就无从知道
    /// 该去列哪个仓库的目录。</para>
    /// </summary>
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    /// <summary>解析出的 git ref（分支/tag/sha），空表示默认分支。</summary>
    public string GitRef { get; init; } = "";

    /// <summary>是否来自某个具体仓库（可用于浏览文件树）。</summary>
    public bool HasRepo => !string.IsNullOrEmpty(Owner) && !string.IsNullOrEmpty(Repo);

    public bool IsMulti => Items.Count > 1;
}

/// <summary>解析失败。</summary>
public sealed class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
}
