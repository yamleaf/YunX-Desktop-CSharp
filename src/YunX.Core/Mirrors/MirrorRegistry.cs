namespace YunX.Core.Mirrors;

/// <summary>
/// 内置线路表。
///
/// <para><b>关于 Enabled 的取值</b>：默认关闭的线路是**在开发机上实测已失效**的
/// （TLS 握手失败 / 连接被拒 / 超时）。保留在列表里是因为不同地区、不同运营商的表现
/// 差异很大，用户可以在设置里自行勾选启用。</para>
///
/// <para><b>实测数据（2026-10，4MB 资产单线程真实吞吐）</b>：</para>
/// <list type="bullet">
///   <item>gh-proxy.com ≈ 1122 KB/s</item>
///   <item>cdn.gh-proxy.org ≈ 974 KB/s</item>
///   <item>ghproxy.net ≈ 312 KB/s</item>
///   <item>ghfast.top ≈ 221 KB/s</item>
///   <item>gh.llkk.cc / github.moeyy.xyz / ghproxy.cc / hub.gitmirror.com → 失效</item>
/// </list>
/// </summary>
public static class MirrorRegistry
{
    public static List<Mirror> CreateDefault()
    {
        return new List<Mirror>
        {
            new()
            {
                // 直连不做任何改写，必须能吃所有类别（含非 GitHub 的 Other，
                // 否则网盘直链会一条线路都没有，只剩 OrderedUrlsAsync 的兜底）
                Id = "direct", Name = "直连 GitHub", Kind = MirrorKind.Direct,
                Scopes = new[]
                {
                    UrlKind.Api, UrlKind.Raw, UrlKind.Archive, UrlKind.Asset,
                    UrlKind.Page, UrlKind.Other,
                },
            },
            new()
            {
                Id = "gh-proxy.com", Name = "gh-proxy.com", Kind = MirrorKind.Prefix,
                Arg = "https://gh-proxy.com/",
            },
            new()
            {
                // 原项目 YunX-Desktop 使用的镜像，实测与 gh-proxy.com 基本持平
                Id = "cdn.gh-proxy.org", Name = "cdn.gh-proxy.org", Kind = MirrorKind.Prefix,
                Arg = "https://cdn.gh-proxy.org/",
            },
            new()
            {
                Id = "ghproxy.net", Name = "ghproxy.net", Kind = MirrorKind.Prefix,
                Arg = "https://ghproxy.net/",
            },
            new()
            {
                Id = "ghfast.top", Name = "ghfast.top", Kind = MirrorKind.Prefix,
                Arg = "https://ghfast.top/",
            },

            // 归档专用：这些代理对 codeload 返回 403，但认 github.com/archive 形式
            new()
            {
                Id = "gh-proxy.com-arch", Name = "gh-proxy.com（归档）",
                Kind = MirrorKind.PrefixArchive, Arg = "https://gh-proxy.com/",
                Scopes = new[] { UrlKind.Archive },
            },
            new()
            {
                Id = "ghfast.top-arch", Name = "ghfast.top（归档）",
                Kind = MirrorKind.PrefixArchive, Arg = "https://ghfast.top/",
                Scopes = new[] { UrlKind.Archive },
            },
            new()
            {
                Id = "ghproxy.net-arch", Name = "ghproxy.net（归档）",
                Kind = MirrorKind.PrefixArchive, Arg = "https://ghproxy.net/",
                Scopes = new[] { UrlKind.Archive },
            },

            // jsDelivr：仅 raw；对单文件很稳，但不支持归档/资产
            new()
            {
                Id = "jsdelivr", Name = "jsDelivr CDN", Kind = MirrorKind.JsDelivr,
                Arg = "cdn.jsdelivr.net", Scopes = new[] { UrlKind.Raw },
            },
            new()
            {
                Id = "jsdelivr-fastly", Name = "jsDelivr (fastly)", Kind = MirrorKind.JsDelivr,
                Arg = "fastly.jsdelivr.net", Scopes = new[] { UrlKind.Raw },
            },

            // 以下为实测失效线路，默认关闭
            new()
            {
                Id = "gh.llkk.cc", Name = "gh.llkk.cc", Kind = MirrorKind.Prefix,
                Arg = "https://gh.llkk.cc/", Enabled = false,
            },
            new()
            {
                Id = "github.moeyy.xyz", Name = "github.moeyy.xyz", Kind = MirrorKind.Prefix,
                Arg = "https://github.moeyy.xyz/", Enabled = false,
            },
            new()
            {
                Id = "ghproxy.cc", Name = "ghproxy.cc", Kind = MirrorKind.Prefix,
                Arg = "https://ghproxy.cc/", Enabled = false,
            },
            new()
            {
                Id = "hub.gitmirror.com", Name = "hub.gitmirror.com", Kind = MirrorKind.Prefix,
                Arg = "https://hub.gitmirror.com/", Enabled = false,
            },
            new()
            {
                Id = "raw.gitmirror.com", Name = "raw.gitmirror.com", Kind = MirrorKind.RawHost,
                Arg = "raw.gitmirror.com", Scopes = new[] { UrlKind.Raw }, Enabled = false,
            },
        };
    }
}
