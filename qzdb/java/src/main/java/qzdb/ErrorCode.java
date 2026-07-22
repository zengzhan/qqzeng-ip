package qzdb;

/**
 * Error codes for QZDB database operations.
 */
public enum ErrorCode {
    NOT_FOUND("Not found"),
    CORRUPTED("Data corrupted"),
    OUT_OF_BOUNDS("Out of bounds"),
    INVALID_PARAM("Invalid parameter"),
    BAD_HEADER("Bad header"),
    BAD_MAGIC("Bad magic number"),
    UNSUPPORTED("Unsupported format version");

    private final String description;

    ErrorCode(String description) {
        this.description = description;
    }

    public String getDescription() {
        return description;
    }
}
