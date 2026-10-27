using Pledge.Shared;
using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Pledge.Receipts;

public enum ReceiptStatus { Issued, Rejected }

public class Receipt
{
    public Guid DonationId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public ReceiptStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ReceiptsDbContext(DbContextOptions<ReceiptsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "receipts";

    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Receipt>(e =>
        {
            e.ToTable("Receipts");
            e.HasKey(x => x.DonationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the receipt once the match are held. A poison reference is committed as rejected.</summary>
public class ReceiptsHandler(ReceiptsDbContext db) : IMessageHandler
{
    public const string Name = "receipts";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Receipts does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<MatchHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, MatchHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Receipts.Add(new Receipt
            {
                DonationId = held.DonationId,
                OwnerId = held.OwnerId,
                Status = ReceiptStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("receipts", Topology.ReleaseKey, held.DonationId, new ReleaseMatch(held.DonationId, "poison"));
            db.Enqueue("receipts", Topology.RejectedKey, held.DonationId, new ReceiptRejected(held.DonationId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"RC-{held.DonationId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Receipts.Add(new Receipt
            {
                DonationId = held.DonationId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = ReceiptStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("receipts", Topology.IssuedKey, held.DonationId, new ReceiptIssued(held.DonationId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
