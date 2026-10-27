using System.Collections.Concurrent;
using Pledge.Pools;
using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Pledge.IntegrationTests;

public class PoolsConcurrencyTests
{
    [Fact]
    public async Task LastCents_TwoConcurrentDonations_TheyShareWhatIsLeftAndNeverMoreThanThePoolHas()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(openingCents: 100);

        var outcomes = new ConcurrentBag<ReserveOutcome>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Pools();
            outcomes.Add(await new PoolsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveMatch(Guid.CreateVersion7(), Guid.NewGuid(), pool.Id, 60, $"PAY-{n}")));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Pools();
        var holds = await check.Holds.ToListAsync();
        Assert.Single(outcomes, o => o == ReserveOutcome.Full);
        Assert.Single(outcomes, o => o == ReserveOutcome.Partial);
        Assert.Equal(new long[] { 40, 60 }, holds.Select(h => h.MatchedCents).OrderBy(c => c).ToArray());
        Assert.Equal(0, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.AvailableCents).FirstAsync());
        Assert.Equal(2, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
    }

    [Fact]
    public async Task ManyConcurrentDonations_TheSumMatchedIsExactlyThePool()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(openingCents: 1_000);

        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 12).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Pools();
            await new PoolsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveMatch(Guid.CreateVersion7(), Guid.NewGuid(), pool.Id, 150, $"PAY-{n}"));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Pools();
        Assert.Equal(1_000, await check.Holds.SumAsync(h => h.MatchedCents));
        Assert.Equal(0, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.AvailableCents).FirstAsync());
        Assert.Equal(12, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task EmptyPool_StillHoldsTheDonation_WithNothingMatched_AndReleaseChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(openingCents: 50);
        await using var db = TestDatabase.Pools();
        var handler = new PoolsHandler(db);
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveMatch(Guid.CreateVersion7(), Guid.NewGuid(), pool.Id, 50, "PAY-1"));

        var donation = Guid.CreateVersion7();
        var outcome = await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveMatch(donation, Guid.NewGuid(), pool.Id, 30, "PAY-2"));
        Assert.Equal(ReserveOutcome.None, outcome);
        Assert.True(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseMatch(donation, "refund")));

        await using var check = TestDatabase.Pools();
        Assert.Equal(0, await check.Holds.Where(h => h.DonationId == donation).Select(h => h.MatchedCents).SingleAsync());
        Assert.Equal(0, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.AvailableCents).FirstAsync());
    }

    [Fact]
    public async Task Release_GivesTheMatchBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(openingCents: 4);
        var donationId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Pools())
            await new PoolsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveMatch(donationId, Guid.NewGuid(), pool.Id, 3, "PAY-1"));

        // Two different commands (poison compensation and a refund) both ask for the same match back.
        await using var first = TestDatabase.Pools();
        await using var second = TestDatabase.Pools();
        var one = await new PoolsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseMatch(donationId, "poison"));
        var two = await new PoolsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseMatch(donationId, "refund"));

        await using var check = TestDatabase.Pools();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.AvailableCents).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(openingCents: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveMatch(Guid.CreateVersion7(), Guid.NewGuid(), pool.Id, 2, "PAY-1");

        await using var a = TestDatabase.Pools();
        await using var b = TestDatabase.Pools();
        var first = await new PoolsHandler(a).ReserveAsync(messageId, command);
        var second = await new PoolsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Pools();
        Assert.Equal(ReserveOutcome.Full, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.AvailableCents).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(openingCents: 2);
        await using var db = TestDatabase.Pools();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Pools.Where(s => s.Id == pool.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableCents, x => x.AvailableCents - 3)));
    }
}
