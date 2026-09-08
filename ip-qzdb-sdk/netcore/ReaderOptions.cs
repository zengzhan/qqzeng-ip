namespace QQZeng.Qzdb;

/// <summary>Options used when opening a QZDB reader.</summary>
public sealed class ReaderOptions
{
    /// <summary>Verify the canonical CRC32 during open. Defaults to true.</summary>
    public bool VerifyCrc { get; set; } = true;

    /// <summary>Zero-based group index to expose. Defaults to the first group.</summary>
    public int GroupIndex { get; set; }

    /// <summary>Optionally touch/warmup index and jump table pages to eliminate cold page faults on first queries. Defaults to false.</summary>
    public bool Warmup { get; set; }
}
