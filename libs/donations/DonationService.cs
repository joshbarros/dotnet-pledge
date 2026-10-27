using Pledge.Shared;
using Pledge.Shared.Auth;
using Pledge.Shared.Contracts;
using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Pledge.Donations;

/// <summary>HTTP side of the saga: writes the donation and its first outbox row in one local transaction.</summary>
public class DonationService(DonationsDbContext db, IPoolCatalog pools, ICurrentUser user)
{
    public async Task<Donation> CreateAsync(CreateDonationRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to send.");
        if (string.IsNullOrWhiteSpace(request.PoolCode))
            throw new DomainException(400, "Pool code is required.");
        var error = DonationRules.Validate(request.Reference, request.AmountCents);
        if (error is not null)
            throw new DomainException(400, error);

        var pool = await pools.FindAsync(request.PoolCode.Trim(), ct)
            ?? throw new DomainException(404, $"Pool '{request.PoolCode}' does not exist.");

        var amountCents = request.AmountCents;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var donation = new Donation
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
            PoolId = pool.Id,
            PoolCode = pool.Code,
            Reference = reference,
            AmountCents = amountCents,
            FeeCents = DonationRules.FeeCents(amountCents),
            Status = DonationStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Donations.Add(donation);
        db.Enqueue("donations", Topology.ReserveKey, donation.Id, new ReserveMatch(donation.Id, ownerId, pool.Id, amountCents, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already used on {pool.Code}.");
        }
        await tx.CommitAsync(ct);
        return donation;
    }

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Donation> GetAsync(Guid id, CancellationToken ct)
    {
        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (donation is null || !OwnerAccess.CanAccess(user, donation.OwnerId))
            throw new DomainException(404, "Donation not found.");
        return donation;
    }

    public async Task<(List<Donation> Items, int Total)> ListAsync(string? poolCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Donations.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see donations.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(poolCode))
            query = query.Where(b => b.PoolCode == poolCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Completed to Refunded and asks Pools to give the match back. The receipt is not touched.</summary>
    public async Task<Donation> RefundAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Donations
            .Where(b => b.Id == id && (b.Status == DonationStatus.Held || b.Status == DonationStatus.Completed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, DonationStatus.Refunded)
                .SetProperty(b => b.Reason, "Refunded by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Donations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A donation in status {current.Status} cannot be refunded.");
        }

        db.Enqueue("donations", Topology.ReleaseKey, id, new ReleaseMatch(id, "refund"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Donations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
