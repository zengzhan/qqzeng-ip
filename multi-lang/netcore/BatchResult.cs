namespace Qzdb;

public readonly record struct BatchResult(
    string Input,
    GeoInfo? Result,
    QzdbException? Error
)
{
    public bool IsSuccess => Error == null && Result != null;
    public bool IsNotFound => Error == null && Result == null;
    public bool HasError => Error != null;
}
