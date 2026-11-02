using AuthHub.Domain.Entities;

namespace AuthHub.Application.Interfaces;

/// <summary>MFA 验证码的一条下发通道。</summary>
/// <remarks>
/// - 每个实现把两件**互相绑定**的事封在一起：<b>投递目标怎么取</b> 与 <b>文案怎么写、走哪个 sender</b> —— 加一条通道 = 加一个实现 + 一行 DI 注册，<c>AccountService</c> 不需要任何改动（改造前这两半挤在 <c>SendTwoFactorCodeAsync</c> 的 <c>switch</c> 里，很容易只补 sender 忘了补"联系方式为空时的判定"）。
/// - 这是**行为**分支，不是查表映射："值 → 标签"的映射继续用 switch 表达式才是对的，硬套多态只会平白多出一堆类。
/// </remarks>
public interface ITwoFactorChannel
{
    /// <summary>
    /// 通道名，必须与 ASP.NET Identity 的 TwoFactorTokenProvider 名**逐字一致**
    /// （内置的是 <c>"Email"</c> 与 <c>"Phone"</c>），否则
    /// <c>UserManager.GenerateTwoFactorTokenAsync</c> 找不到对应的 provider。
    /// </summary>
    string Provider { get; }

    /// <summary>取该通道的投递目标；用户没留对应联系方式时返回 <c>null</c>。</summary>
    string? ResolveTarget(ApplicationUser user);

    /// <summary>把验证码送到目标。</summary>
    Task SendAsync(string target, string code, CancellationToken cancellationToken);
}
