namespace QQZeng.Qzdb;

/// <summary>Per-row result carrying the three-state lookup semantics of find().</summary>
/// <param name="Info">Resolved GeoInfo on success; null when not found or on error.</param>
/// <param name="Error">The error (e.g. InvalidIp / Corrupted) when the lookup failed; null on success or not-found.</param>
/// <param name="Input">The original IP string this result corresponds to (null for single find); lets callers trace batch/stream results back to their input.</param>
public readonly record struct BatchResult(GeoInfo? Info, QzdbException? Error, string? Input = null)
{
    public bool IsSuccess => Error == null && Info != null;
    public bool IsNotFound => Error == null && Info == null;
    public bool HasError => Error != null;
}
