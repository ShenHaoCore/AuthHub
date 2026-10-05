using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server;

namespace AuthHub.Api.Extensions;

/// <summary>
/// OpenIddict 签名 / 加密密钥的配置。
///
/// 优先级：
///   1) 配置了持久化证书（AuthHub:Keys:SigningCertificatePath）→ 使用它（生产唯一推荐方式，建议从密钥管理服务注入）；
///   2) Testing 环境 → 临时密钥（进程内生成，不写证书存储，适合 CI）；
///   3) 其他环境 → 开发证书；若运行环境不允许访问证书存储（Linux 容器 / 受限沙箱）→ 退化为临时密钥。
///
/// 注意：临时密钥在进程重启后会变化，已颁发的令牌全部失效。生产必须走路径 1。
/// </summary>
internal static class OpenIddictKeySetup
{
    public static string ConfigureKeys(
        this OpenIddictServerBuilder options,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var certificatePath = configuration["AuthHub:Keys:SigningCertificatePath"];
        var certificatePassword = configuration["AuthHub:Keys:SigningCertificatePassword"];

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!File.Exists(certificatePath))
            {
                throw new FileNotFoundException(
                    $"配置了 AuthHub:Keys:SigningCertificatePath={certificatePath}，但文件不存在。生产环境必须提供有效的签名证书。",
                    certificatePath);
            }

            // PFX/PKCS#12 文件：同时用于签名与加密
            var certificate = new X509Certificate2(
                certificatePath,
                certificatePassword,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);

            options.AddEncryptionCertificate(certificate);
            options.AddSigningCertificate(certificate);

            return $"持久化证书（{certificate.Subject}，指纹 {certificate.Thumbprint[..8]}…）";
        }

        if (environment.IsEnvironment("Testing"))
        {
            options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            return "临时密钥（Testing 环境）";
        }

        try
        {
            options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
            return "开发证书（自动生成并存入当前用户证书存储）";
        }
        catch (Exception)
        {
            // 容器 / 受限环境无法访问证书存储时的兜底，避免服务完全起不来
            options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            return "临时密钥（无法访问证书存储，已降级。生产环境请配置持久化证书）";
        }
    }
}
