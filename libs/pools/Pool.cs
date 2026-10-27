namespace Pledge.Pools;

public class Pool
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Sponsor { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Cause { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
    public long OpeningCents { get; set; }
    public long AvailableCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class MatchHold
{
    public Guid DonationId { get; set; }
    public Guid PoolId { get; set; }
    /// <summary>What the pool actually matched: at most the donation, less when the pool was running out.</summary>
    public long MatchedCents { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
