using Microsoft.AspNetCore.Identity;

namespace AuthHub.Application.Common;

/// <summary>
/// <see cref="IdentityResult"/> → <see cref="Error"/> 的**唯一**映射点。
///
/// 为什么值得单独一个类：这段映射原先在 AccountService 与 UserAdminService 里各写了一遍
/// （其中 AccountService 那份还带了个从未被用到的 <c>fallbackCode</c> 死参数），
/// RoleAdminService 又内联了三处简化版 —— 简化版丢掉了按 Code 分组的
/// <see cref="Error.ValidationErrors"/>，于是同样一次「角色名含非法字符」，
/// 走 API 能展开到字段、走后台页面只有一个笼统提示。收在一处后只有一种语义。
/// </summary>
public static class IdentityErrorMapper
{
    /// <summary>把 Identity 的失败结果翻译成带字段错误的 <see cref="Error"/>；成功时返回 <see cref="Error.None"/>。</summary>
    public static Error ToError(this IdentityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Succeeded)
        {
            return Error.None;
        }

        // 按 Error.Code 分组：Api 层的 ValidationFilter 会把它展开成 { 字段: [消息] }，
        // 与 FluentValidation 的输出形状一致 —— 调用方不必按校验来源分两套解析。
        var errors = result.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());

        var message = string.Join(" ", result.Errors.Select(error => error.Description));

        // Description 理论上可能为空（自定义 IdentityErrorDescriber），兜一句人类可读的文案，
        // 免得后台页面弹出一个没有任何文字的错误框。
        return Error.Validation(string.IsNullOrWhiteSpace(message) ? "操作失败。" : message, errors);
    }
}
