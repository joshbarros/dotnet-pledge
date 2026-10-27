using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Pledge.Pools;

/// <summary>Full: the whole donation was matched. Partial: the pool had less left. None: the pool was empty (the donation still goes through).</summary>
public enum ReserveOutcome { Full, Partial, None, Duplicate }

/// <summary>
/// The only writer of Pool.AvailableCents and of match holds.
/// The reservation is one locking UPDATE that takes LEAST(available cents, donation): a donation is matched in full, in part, or not at all, and never beyond what the pool has.
/// </summary>
public class PoolsHandler(PoolsDbContext db) : IMessageHandler
{
    public const string Name = "pools";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveMatch>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseMatch>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Pools does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveMatch command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        // One statement locks the pool row, takes LEAST(available, donation) and returns what it took. Two concurrent
        // donations queue on the row lock, so together they can never take more than the pool had.
        var taken = await db.Database.SqlQuery<long>($"""
            WITH cur AS (SELECT "Id", "AvailableCents" FROM pools."Pools" WHERE "Id" = {command.PoolId} FOR UPDATE)
            UPDATE pools."Pools" p SET "AvailableCents" = p."AvailableCents" - LEAST(cur."AvailableCents", {command.AmountCents})
            FROM cur WHERE p."Id" = cur."Id"
            RETURNING LEAST(cur."AvailableCents", {command.AmountCents}) AS "Value"
            """).ToListAsync(ct);
        var matched = taken.Count == 1 ? taken[0] : 0;

        db.Holds.Add(new MatchHold
        {
            DonationId = command.DonationId,
            PoolId = command.PoolId,
            MatchedCents = matched,
            Status = HoldStatus.Held,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Enqueue("pools", Topology.HeldKey, command.DonationId,
            new MatchHeld(command.DonationId, command.OwnerId, command.PoolId, matched, command.Reference));
        var outcome = matched == command.AmountCents ? ReserveOutcome.Full : matched > 0 ? ReserveOutcome.Partial : ReserveOutcome.None;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held match back exactly once. A second release for the same donation changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseMatch command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.DonationId == command.DonationId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.DonationId == command.DonationId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Pools
                    .Where(s => s.Id == hold.PoolId && s.AvailableCents + hold.MatchedCents <= s.OpeningCents)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableCents, x => x.AvailableCents + hold.MatchedCents), ct);
                db.Enqueue("pools", Topology.ReleasedKey, command.DonationId,
                    new MatchReleased(command.DonationId, hold.MatchedCents, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
