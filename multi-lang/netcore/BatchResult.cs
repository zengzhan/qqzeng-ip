namespace Qzdb;

/// <summary>Per-row result carrying the three-state lookup semantics of find().</summary>
public readonly record struct BatchResult(GeoInfo? Info, QzdbException? Error)
{
    public bool IsSuccess => Error == null && Info != null;
    public bool IsNotFound => Error == null && Info == null;
    public bool HasError => Error != null;
}