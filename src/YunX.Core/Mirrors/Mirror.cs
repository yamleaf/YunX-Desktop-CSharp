namespace YunX.Core.Mirrors;

/// <summary>URL 分类：决定哪些线路能处理它。</summary>
public enum UrlKind
{
    /// <summary>GitHub API（api.github.com）。</summary>
    Api,
    /// <summary>raw 文件（raw.githubusercontent.com）。</summary>
    Raw,
    /// <summary>仓库归档（codeload.github.com）。</summary>
    Archive,
    /// <summary>Release 资产（github.com/.../releases/download/... 及 CDN）。</summary>
    Asset,
    /// <summary>普通网页。</summary>
    Page,

    /// <summary>
    /// 与 GitHub 无关的直链（网盘 CDN、任意第三方站点）。
    ///
    /// <para><b>必须单独分类</b>：早期实现把「不认识的 host」一律归为 <see cref="Asset"/>，
    /// 于是网盘直链（如 <c>cdn.quark.cn/...</c>）也会被 5 条 GitHub 镜像前缀成
    /// <c>https://gh-proxy.com/https://cdn.quark.cn/...</c>。这些请求必然失败，
    /// 但探测阶段会先挨个试一遍（最多 4 条），白等好几秒才轮到直连。
    /// 归到 Other 后，镜像的 <c>Supports</c> 全部为 false，线路池只剩直连。</para>
    /// </summary>
    Other,
}

/// <summary>线路类型。</summary>
public enum MirrorKind
{
    /// <summary>直连，不做任何改写。</summary>
    Direct,
    /// <summary>前缀代理：<c>{prefix}/{原URL}</c>，对 raw/归档/资产普遍有效。</summary>
    Prefix,
    /// <summary>
    /// 前缀代理 + 归档改写：先把 <c>codeload.../zip/refs/heads/x</c> 改写成
    /// <c>github.com/o/r/archive/refs/heads/x.zip</c> 再加前缀。
    /// 为什么要单独一类：实测多个镜像对 codeload 直接返回 403，但对 archive 形式正常。
    /// </summary>
    PrefixArchive,
    /// <summary>替换 raw 域名（如 <c>raw.gitmirror.com</c>）。</summary>
    RawHost,
    /// <summary>jsDelivr：<c>cdn.jsdelivr.net/gh/o/r@ref/path</c>，仅 raw 可用。</summary>
    JsDelivr,
}

/// <summary>
/// 一条下载线路。
/// </summary>
public sealed class Mirror
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public MirrorKind Kind { get; init; }
    /// <summary>前缀或替换后的 host，取决于 <see cref="Kind"/>。</summary>
    public string Arg { get; init; } = "";
    /// <summary>该线路支持的 URL 类别；默认全部（具体 <see cref="Kind"/> 再按需过滤）。</summary>
    public UrlKind[] Scopes { get; init; } =
    {
        UrlKind.Api, UrlKind.Raw, UrlKind.Archive, UrlKind.Asset, UrlKind.Page,
    };
    public bool Enabled { get; init; } = true;

    // ---- 运行时健康度（不持久化）----
    /// <summary>连续失败次数，成功即清零。</summary>
    public int FailCount { get; set; }
    /// <summary>冷却结束时刻（UTC）。在此之前不参与择优。</summary>
    public DateTimeOffset OfflineUntil { get; set; } = DateTimeOffset.MinValue;

    public bool IsCoolingDown => OfflineUntil > DateTimeOffset.UtcNow;

    public bool Supports(string url)
    {
        if (!Enabled) return false;
        return Scopes.Contains(UrlClassifier.Classify(url));
    }

    /// <summary>
    /// 把原始 URL 转成该线路的 URL；不适用返回 null。
    /// </summary>
    public string? Build(string url)
    {
        var kind = UrlClassifier.Classify(url);
        if (!Scopes.Contains(kind)) return null;

        switch (Kind)
        {
            case MirrorKind.Direct:
                return url;

            case MirrorKind.Prefix:
                return string.IsNullOrEmpty(Arg) ? null : Arg.TrimEnd('/') + "/" + url;

            case MirrorKind.PrefixArchive:
                if (string.IsNullOrEmpty(Arg)) return null;
                if (kind != UrlKind.Archive) return null;
                var converted = UrlClassifier.CodeloadToArchive(url) ?? url;
                return Arg.TrimEnd('/') + "/" + converted;

            case MirrorKind.RawHost:
                if (kind != UrlKind.Raw) return null;
                return ReplaceRawHost(url, Arg);

            case MirrorKind.JsDelivr:
                if (kind != UrlKind.Raw) return null;
                return ToJsDelivr(url, Arg);

            default:
                return null;
        }
    }

    private static string? ReplaceRawHost(string url, string newHost)
    {
        const string oldHost = "https://raw.githubusercontent.com";
        if (!url.StartsWith(oldHost, StringComparison.OrdinalIgnoreCase)) return null;
        return $"https://{newHost}" + url[oldHost.Length..];
    }

    private static string? ToJsDelivr(string url, string host)
    {
        // raw.githubusercontent.com/{owner}/{repo}/{ref}/{path} → cdn.jsdelivr.net/gh/{owner}/{repo}@{ref}/{path}
        const string prefix = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[prefix.Length..];
        var parts = rest.Split('/', 4);
        if (parts.Length < 4) return null;
        var h = string.IsNullOrEmpty(host) ? "cdn.jsdelivr.net" : host;
        return $"https://{h}/gh/{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}";
    }
}

/// <summary>URL 分类与形态改写工具。</summary>
public static class UrlClassifier
{
    public static UrlKind Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return UrlKind.Page;

        string host = u.Host.ToLowerInvariant();
        string path = u.AbsolutePath;

        if (host == "api.github.com") return UrlKind.Api;
        if (host == "raw.githubusercontent.com") return UrlKind.Raw;
        if (host == "codeload.github.com") return UrlKind.Archive;
        if (host.EndsWith("githubusercontent.com")) return UrlKind.Asset;
        if (path.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
            return UrlKind.Asset;
        if (host is "github.com" or "www.github.com" or "gist.github.com") return UrlKind.Page;

        // 只有「像 release 资产」的才归 Asset（此前是无条件 Asset，
        // 会把所有第三方直链都当成 GitHub 资产交给镜像处理）
        return UrlKind.Other;
    }

    /// <summary>
    /// codeload 归档直链 → github.com/archive 等价形式。
    /// 给只认后者、对 codeload 返回 403 的代理用。
    /// </summary>
    public static string? CodeloadToArchive(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (!u.Host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase)) return null;

        // /{owner}/{repo}/{zip|tar.gz}/{rest}
        var seg = u.AbsolutePath.Trim('/').Split('/');
        if (seg.Length < 4) return null;
        string owner = seg[0], repo = seg[1], fmt = seg[2];
        if (fmt is not ("zip" or "tar.gz")) return null;

        var rest = string.Join('/', seg.Skip(3));
        if (rest.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return $"https://github.com/{owner}/{repo}/archive/HEAD.{fmt}";
        return $"https://github.com/{owner}/{repo}/archive/{rest}.{fmt}";
    }
}
