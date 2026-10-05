namespace AuthHub.Application.Common;

/// <summary>错误分类，用于 Api 层映射 HTTP 状态码。</summary>
public enum ErrorType
{
    None = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
    LockedOut = 6,
    Failure = 7
}

/// <summary>统一错误描述。</summary>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Failure,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    public static Error Validation(string message, IReadOnlyDictionary<string, string[]>? errors = null)
        => new("ValidationFailed", message, ErrorType.Validation, errors);

    public static Error NotFound(string message) => new("NotFound", message, ErrorType.NotFound);

    public static Error Conflict(string message) => new("Conflict", message, ErrorType.Conflict);

    public static Error Unauthorized(string message) => new("Unauthorized", message, ErrorType.Unauthorized);

    public static Error Forbidden(string message) => new("Forbidden", message, ErrorType.Forbidden);

    public static Error LockedOut(string message) => new("LockedOut", message, ErrorType.LockedOut);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}

/// <summary>
/// 应用层统一返回值。Api 层据此映射为 200/400/401/403/404/409。
/// 目的是把“业务流程结果”与“HTTP 语义”解耦，便于单元测试。
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
            throw new InvalidOperationException("成功的 Result 不允许携带错误。");
        if (!isSuccess && error == Error.None)
            throw new InvalidOperationException("失败的 Result 必须携带错误。");

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <summary>带返回值的 Result。</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>成功时返回业务值，失败时抛异常（调用方必须先判断 IsSuccess）。</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("失败的 Result 不包含值，请先检查 IsSuccess。");

    public static implicit operator Result<TValue>(TValue value) => Success(value);
}
