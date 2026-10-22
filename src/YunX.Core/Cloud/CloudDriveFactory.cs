using YunX.Core.Cloud;
using YunX.Core.Cloud.Drives;
using YunX.Core.Net;

namespace YunX.Core.Cloud;

/// <summary>
/// 网盘装配工厂：按当前配置创建所有适配器并注册。
///
/// <para>凭证从 <see cref="Data.CredentialRepository"/> 取（已解密），
/// 引擎层不关心存储细节。</para>
/// </summary>
public static class CloudDriveFactory
{
    /// <summary>
    /// 创建注册表。
    /// </summary>
    /// <param name="client">HTTP 客户端（各适配器共用连接池）。</param>
    /// <param name="credentialOf">按平台取凭证（返回已解密的 Cookie / Token）。</param>
    /// <param name="client">HTTP 客户端（各适配器共用连接池）。</param>
    /// <param name="credentialOf">按平台取凭证（返回已解密的 Cookie / Token）。</param>
    /// <param name="noRedirectClient">
    /// 禁用自动重定向的客户端，只给蓝奏用（它要逐跳校验重定向域名，防 SSRF）。
    /// 传 null 时蓝奏会退回自建 handler，代价是丢掉 hosts 绕过与共享连接池。
    /// </param>
    public static CloudRegistry Create(HttpClient client, Func<string, string?> credentialOf,
        HttpClient? noRedirectClient = null)
    {
        var registry = new CloudRegistry();

        // 注册顺序 = 识别优先级。规则越宽的排越后，
        // 否则会把别的平台链接吞掉（迅雷/蓝奏的识别规则最宽，放最后）。
        registry.Register(new Pan123Drive(client, () => credentialOf(CloudPlatforms.Pan123)));
        registry.Register(new QuarkDrive(client, () => credentialOf(CloudPlatforms.Quark)));
        registry.Register(new UCDrive(client, () => credentialOf(CloudPlatforms.UC)));
        registry.Register(new C139Drive(client, () => credentialOf(CloudPlatforms.C139)));
        registry.Register(new BaiduDrive(client, () => credentialOf(CloudPlatforms.Baidu)));
        registry.Register(new GuangYaDrive(client, () => credentialOf(CloudPlatforms.GuangYa)));
        registry.Register(new Pan115Drive(client, () => credentialOf(CloudPlatforms.Pan115)));

        // 蓝奏域名族很杂，且它没有固定的路径特征（分享码就是任意一段），
        // 因此必须靠域名族精确判断 —— 放在 115 之后，避免抢占。
        registry.Register(new LanzouDrive(client, () => credentialOf(CloudPlatforms.Lanzou),
            noRedirectClient));

        // 迅雷的「中文口令」规则最宽（任何非链接文本都算），必须最后注册
        registry.Register(new XunleiDrive(client, () => credentialOf(CloudPlatforms.Xunlei)));

        return registry;
    }
}
