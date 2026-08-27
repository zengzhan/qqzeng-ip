namespace QQZeng.Qzdb;

/// <summary>Error categories raised by the QZDB reader and registry.</summary>
public enum ErrorCode
{
    /// <summary>The QZDB file could not be located or opened for reading.</summary>
    FileNotFound,
    /// <summary>The file header magic bytes are not "QZDB".</summary>
    BadMagic,
    /// <summary>The file header size or structure is invalid.</summary>
    BadHeader,
    /// <summary>The database format version is not supported by this reader.</summary>
    Unsupported,
    /// <summary>The file is internally inconsistent or failed its CRC32 check.</summary>
    Corrupted,
    /// <summary>A caller-supplied parameter (for example groupIndex) was invalid.</summary>
    InvalidParam,
    /// <summary>The requested entry was not present (used by some lookup paths).</summary>
    NotFound,
    /// <summary>The supplied IP string or bytes could not be parsed.</summary>
    InvalidIp
}

/// <summary>Exception type thrown by the QZDB SDK; carries an <see cref="ErrorCode"/> describing the failure category.</summary>
public class QzdbException : Exception
{
    /// <summary>The machine-readable category of this error.</summary>
    public ErrorCode ErrorCode { get; }

    /// <summary>Creates an exception for the given error code and message.</summary>
    public QzdbException(ErrorCode code, string message) : base(message) => ErrorCode = code;
    /// <summary>Creates an exception that wraps an inner exception.</summary>
    public QzdbException(ErrorCode code, string message, Exception inner) : base(message, inner) => ErrorCode = code;

    // 标准 Exception 构造面（CA1032）：供序列化、泛化封装与无特定错误码的抛出场景使用。
    // 默认 ErrorCode 取 InvalidParam（"输入/参数无效"是比 NotFound（"查询未命中"）更中性的缺省，
    // 避免调用方把初始化/封装错误误判为一次正常的未命中）。
    /// <summary>Creates a generic exception with a default InvalidParam code.</summary>
    public QzdbException() : this(ErrorCode.InvalidParam, "QZDB error.") { }
    /// <summary>Creates an exception with a default InvalidParam code and the given message.</summary>
    public QzdbException(string message) : this(ErrorCode.InvalidParam, message) { }
    /// <summary>Creates an exception that wraps an inner exception with a default InvalidParam code.</summary>
    public QzdbException(string message, Exception innerException) : this(ErrorCode.InvalidParam, message, innerException) { }
}
