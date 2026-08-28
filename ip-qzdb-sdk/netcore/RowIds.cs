namespace QQZeng.Qzdb;

/// <summary>Reverse-lookup row identifiers for a resolved IP entry. Legacy compatibility struct; the canonical API returns a named tuple from <see cref="QzdbReader.LookupIds(uint)"/>.</summary>
/// <param name="GeoId">Index into the geo string pool for this entry.</param>
/// <param name="AsnId">Index into the ASN string pool (0 when the group has no ASN dimension).</param>
/// <param name="UsageId">Index into the usage-type string pool (0 when the group has no usage dimension).</param>
public readonly record struct RowIds(int GeoId, int AsnId, int UsageId);
