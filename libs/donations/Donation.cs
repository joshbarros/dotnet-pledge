namespace Pledge.Donations;

public enum DonationStatus { Requested, Held, Completed, Refunded, Rejected }

public class Donation
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid PoolId { get; set; }
    public string PoolCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public long AmountCents { get; set; }
    public long FeeCents { get; set; }
    public long MatchedCents { get; set; }
    public DonationStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ReceiptNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? MatchReleasedAt { get; set; }
}

public class DonationOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateDonationRequest(string? PoolCode, string? Reference, long AmountCents);

public record DonationDto(
    Guid Id, Guid OwnerId, string PoolCode, string Reference, long AmountCents, long FeeCents, long MatchedCents, string Status,
    string? Reason, string? ReceiptNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? MatchReleasedAt)
{
    public static DonationDto From(Donation b) => new(
        b.Id, b.OwnerId, b.PoolCode, b.Reference, b.AmountCents, b.FeeCents, b.MatchedCents, b.Status.ToString(),
        b.Reason, b.ReceiptNumber, b.CreatedAt, b.UpdatedAt, b.MatchReleasedAt);
}
