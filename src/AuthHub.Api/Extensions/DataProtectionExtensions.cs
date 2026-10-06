using Microsoft.AspNetCore.DataProtection;

namespace AuthHub.Api.Extensions;

/// <summary>
/// Data Protection 密钥环的配置。
///
/// <para><b>为什么必须显式配置</b>：密钥环保护的不只是"记住我"那类小玩意 —— 本服务里它同时承载
/// 三样东西：会话 Cookie 的加密票、防伪令牌、以及 <c>ITwoFactorTicketProtector</c> 的 MFA 票据。
/// 默认密钥环落在 <c>$HOME/.aspnet/DataProtection-Keys</c>（Windows 上是
/// <c>%LOCALAPPDATA%\ASP.NET\DataProtection-Keys</c>），而容器里这个位置通常**不在卷上**：
/// 容器一重建密钥环就换了一套，于是所有在线用户被登出、手上正在填的表单提交全部 400、
/// 走到一半的 MFA 流程失效。多实例部署时更严重 —— A 实例发的 Cookie 到 B 实例直接解不开。</para>
///
/// <para><b>为什么固定 SetApplicationName</b>：密钥环的默认隔离键由**内容根路径**派生，
/// 两个实例若从不同路径启动（镜像 WORKDIR 不同、挂载点不同）就认不到同一个环，
/// 于是"共享了密钥目录"也依然互相解不开。固定成常量后，只要密钥目录共享就能互通。</para>
/// </summary>
internal static class DataProtectionExtensions
{
    /// <summary>
    /// 密钥环的应用名（隔离键）。**改了会让所有既有会话与防伪令牌立即失效**，
    /// 因此当作常量对待：它是一份需要跨版本、跨实例稳定的约定，不是可调参数。
    /// </summary>
    internal const string ApplicationName = "AuthHub";

    /// <summary>
    /// 注册 Data Protection，并返回描述字符串留给启动日志。
    ///
    /// 没配持久化位置时**不静默**：生产环境会把警告写进返回的描述里，由启动日志带出去
    /// （这是本项目一贯的做法 —— 部署期的问题要落在启动日志第一屏，而不是等运行时暴露）。
    /// 目录配了但不可写则**直接抛**，理由见 <see cref="EnsureWritableDirectory"/>。
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
    /// 确保密钥目录存在，并且**真的能写**。
    ///
    /// 为什么除了建目录还要写一个探针文件：<c>Directory.CreateDirectory</c> 对"已存在但只读"的目录
    /// 是**静默成功**的，而"挂了个只读卷"恰恰是容器里最常见的错配。不探这一下，
    /// 故障会一直拖到第一次签发 Cookie 时才以别的面目出现，排查方向很难指回挂载配置。
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
