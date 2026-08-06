package com.qqzeng.qzdb;

/**
 * QZDB SDK 统一非受检异常
 */
public class QzdbException extends RuntimeException {
    private static final long serialVersionUID = 1L;

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

    @Override
    public String toString() {
        return "QzdbException{" +
                "errorCode=" + errorCode +
                ", message=" + getMessage() +
                '}';
    }
}
