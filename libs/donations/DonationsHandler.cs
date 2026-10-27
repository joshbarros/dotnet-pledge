using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Pledge.Donations;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a donation forward. Each step runs only from the status it expects.</summary>
public class DonationsHandler(DonationsDbContext db, IOptions<DonationOptions> options) : IMessageHandler
{
    public const string Name = "donations";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<MatchHeld>(d.Body), ct),
        Topology.IssuedKey => MarkCompletedAsync(d.MessageId, Json.Parse<ReceiptIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<ReceiptRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<MatchReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Donations does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, MatchHeld m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Donations
            .Where(b => b.Id == m.DonationId && b.Status == DonationStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, DonationStatus.Held)
                .SetProperty(b => b.MatchedCents, m.MatchedCents)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a refund got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkCompletedAsync(Guid messageId, ReceiptIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Donations
            .Where(b => b.Id == m.DonationId && b.Status == DonationStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, DonationStatus.Completed)
                .SetProperty(b => b.ReceiptNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            donationIdForOrderCheck: m.DonationId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, ReceiptRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Donations
            .Where(b => b.Id == m.DonationId && (b.Status == DonationStatus.Held || b.Status == DonationStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, DonationStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, MatchReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Donations
            .Where(b => b.Id == m.DonationId && b.MatchReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.MatchReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? donationIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && donationIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Donations.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == DonationStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
