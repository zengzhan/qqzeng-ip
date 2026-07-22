package qzdb;

/**
 * Exception thrown when QZDB database operations fail.
 * Contains an error code for programmatic error handling.
 */
public class QzdbException extends RuntimeException {
    private final ErrorCode errorCode;

    public QzdbException(ErrorCode errorCode, String message) {
        super(message);
        this.errorCode = errorCode;
    }

    public QzdbException(ErrorCode errorCode, String message, Throwable cause) {
        super(message, cause);
        this.errorCode = errorCode;
    }

    public ErrorCode getErrorCode() {
        return errorCode;
    }
}
