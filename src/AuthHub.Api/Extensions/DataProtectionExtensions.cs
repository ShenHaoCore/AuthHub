using Microsoft.AspNetCore.DataProtection;

namespace AuthHub.Api.Extensions;

/// <summary>
/// Data Protection 密钥环的配置（会话 Cookie 的加密票、防伪令牌、MFA 票据都由它承载）。
/// </summary>
/// <remarks>
/// - 位置必须显式配置：默认密钥环在容器里通常不在卷上，重建即换一套 ——
///   所有在线用户被登出、表单提交全部 400、MFA 流程失效；多实例下 A 发的 Cookie B 解不开。
/// - 固定 SetApplicationName：默认隔离键由内容根路径派生，不同路径启动的实例认不到同一个环；
///   固定成常量后，只要密钥目录共享就能互通。
/// </remarks>
internal static class DataProtectionExtensions
{
    /// <summary>
    /// 密钥环的应用名（隔离键）。改了会让所有既有会话与防伪令牌立即失效，
    /// 因此当作跨版本、跨实例稳定的约定对待，不是可调参数。
    /// </summary>
    internal const string ApplicationName = "AuthHub";

    /// <summary>
    /// 注册 Data Protection，并返回描述字符串留给启动日志。
    /// 未配持久化位置时不静默：生产环境把警告写进返回描述；目录配了但不可写则直接抛，见 <see cref="EnsureWritableDirectory"/>。
    /// </summary>
    public static string AddAuthHubDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        var keysPath = configuration["AuthHub:Security:DataProtectionKeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
        {
            return environment.IsProduction()
                ? "默认位置 ⚠ 未配置 AuthHub:Security:DataProtectionKeysPath —— 容器重建 / 滚动更新会让全部会话、防伪令牌与 MFA 票据失效"
                : "默认位置（开发环境）";
        }

        // 在这里解析成绝对路径并落到描述里，方便运维一眼核对"容器里到底写到了哪儿"。
        var fullPath = Path.GetFullPath(keysPath);
        EnsureWritableDirectory(fullPath);

        builder.PersistKeysToFileSystem(new DirectoryInfo(fullPath));
        return $"持久化到 {fullPath}";
    }

    /// <summary>
    /// 确保密钥目录存在，并且真的能写（探针文件）：
    /// <c>Directory.CreateDirectory</c> 对"已存在但只读"的目录静默成功，而"挂了个只读卷"
    /// 恰恰是容器里最常见的错配，不探这一下故障会拖到第一次签发 Cookie 才暴露。
    /// </summary>
    private static void EnsureWritableDirectory(string fullPath)
    {
        try
        {
            Directory.CreateDirectory(fullPath);

            var probe = Path.Combine(fullPath, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Data Protection 密钥环目录不可用：{fullPath}（{ex.Message}）。" +
                "该目录必须存在且可写 —— 否则所有会话 Cookie、防伪令牌与 MFA 票据都会失效。" +
                "容器部署请把卷挂到这个路径，或修正配置 AuthHub:Security:DataProtectionKeysPath。",
                ex);
        }
    }
}
