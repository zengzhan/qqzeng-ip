namespace QQZeng.Qzdb;

public enum ErrorCode
{
    FileNotFound,
    BadMagic,
    BadHeader,
    Unsupported,
    Corrupted,
    InvalidParam,
    NotFound,
    InvalidIp
}

public class QzdbException : Exception
{
    public ErrorCode ErrorCode { get; }

    public QzdbException(ErrorCode code, string message) : base(message) => ErrorCode = code;
    public QzdbException(ErrorCode code, string message, Exception inner) : base(message, inner) => ErrorCode = code;
}
