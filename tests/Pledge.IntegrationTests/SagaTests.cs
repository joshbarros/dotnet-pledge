using Pledge.Donations;
using Pledge.Receipts;
using Pledge.Shared;
using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Pledge.IntegrationTests;

public class SagaTests
{
    private static DonationsHandler Handler(DonationsDbContext db) => new(db, Options.Create(new DonationOptions()));

    [Fact]
    public async Task HeldMark_RecordsHowMuchWasMatched_AndOnlyFromRequested()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var donation = await TestDatabase.AddDonationAsync(pool, "PAY-M-1", 9, DonationStatus.Requested);
        var message = new MatchHeld(donation.Id, donation.OwnerId, pool.Id, 5, donation.Reference);

        await using (var db = TestDatabase.Donations())
            Assert.Equal(MarkOutcome.Marked, await Handler(db).MarkHeldAsync(Guid.CreateVersion7(), message));
        await using (var db = TestDatabase.Donations())
            Assert.Equal(MarkOutcome.Skipped, await Handler(db).MarkHeldAsync(Guid.CreateVersion7(), message with { MatchedCents = 1 }));

        await using var check = TestDatabase.Donations();
        var row = await check.Donations.SingleAsync(d => d.Id == donation.Id);
        Assert.Equal(DonationStatus.Held, row.Status);
        Assert.Equal(5, row.MatchedCents); // 9 donated, the pool had 5 left
    }

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var donation = await TestDatabase.AddDonationAsync(pool, "PAY-POISON-1", 2, DonationStatus.Held);
        var held = new MatchHeld(donation.Id, donation.OwnerId, pool.Id, 2, donation.Reference);

        await using var docs = TestDatabase.Receipts();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new ReceiptsHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Receipts();
        var doc = await check.Receipts.SingleAsync(d => d.DonationId == donation.Id);
        Assert.Equal(ReceiptStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(donation.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Refund_ThenFinalMark_LeavesDonationRefundedAndReceiptIssued()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var owner = Guid.CreateVersion7();
        var donation = await TestDatabase.AddDonationAsync(pool, "PAY-1", 2, DonationStatus.Held, owner);

        // The receipt is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Receipts())
            await new ReceiptsHandler(docs).IssueAsync(Guid.CreateVersion7(), new MatchHeld(donation.Id, owner, pool.Id, 2, donation.Reference));

        // The owner refunds first.
        await using (var donations = TestDatabase.Donations())
            await new DonationService(donations, new FixedCatalog(pool), new TestUser(owner)).RefundAsync(donation.Id, default);

        await using var late = TestDatabase.Donations();
        var mark = await Handler(late).MarkCompletedAsync(Guid.CreateVersion7(), new ReceiptIssued(donation.Id, "RC-TEST"));

        await using var check = TestDatabase.Donations();
        await using var checkDocs = TestDatabase.Receipts();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(DonationStatus.Refunded, (await check.Donations.SingleAsync()).Status);
        Assert.Equal(ReceiptStatus.Issued, (await checkDocs.Receipts.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var held = await TestDatabase.AddDonationAsync(pool, "PAY-A", 1, DonationStatus.Held);
        var rejected = await TestDatabase.AddDonationAsync(pool, "PAY-B", 1, DonationStatus.Rejected);
        var requested = await TestDatabase.AddDonationAsync(pool, "PAY-C", 1, DonationStatus.Requested);

        await using (var a = TestDatabase.Donations())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkCompletedAsync(Guid.CreateVersion7(), new ReceiptIssued(held.Id, "RC-1")));
        await using (var b = TestDatabase.Donations())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkCompletedAsync(Guid.CreateVersion7(), new ReceiptIssued(rejected.Id, "RC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Donations())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkCompletedAsync(Guid.CreateVersion7(), new ReceiptIssued(requested.Id, "RC-3")));

        await using var check = TestDatabase.Donations();
        var statuses = (await check.Donations.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(DonationStatus.Completed, statuses["PAY-A"]);
        Assert.Equal(DonationStatus.Rejected, statuses["PAY-B"]);
        Assert.Equal(DonationStatus.Requested, statuses["PAY-C"]);
    }

    [Fact]
    public async Task Refund_OfARejectedDonation_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var owner = Guid.CreateVersion7();
        var donation = await TestDatabase.AddDonationAsync(pool, "PAY-1", 1, DonationStatus.Rejected, owner);

        await using var db = TestDatabase.Donations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new DonationService(db, new FixedCatalog(pool), new TestUser(owner)).RefundAsync(donation.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Refund_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var donation = await TestDatabase.AddDonationAsync(pool, "PAY-1", 1, DonationStatus.Held);

        await using var db = TestDatabase.Donations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new DonationService(db, new FixedCatalog(pool), new TestUser(Guid.NewGuid())).RefundAsync(donation.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Donations();
        Assert.Equal(DonationStatus.Held, (await check.Donations.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanRefundAnyonesDonation()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var donation = await TestDatabase.AddDonationAsync(pool, "PAY-1", 1, DonationStatus.Held);

        await using var db = TestDatabase.Donations();
        var refunded = await new DonationService(db, new FixedCatalog(pool), new TestUser(Guid.NewGuid(), staff: true)).RefundAsync(donation.Id, default);
        Assert.Equal(DonationStatus.Refunded, refunded.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Donations();
        var donation = await new DonationService(db, new FixedCatalog(pool), new TestUser(owner))
            .CreateAsync(new CreateDonationRequest(pool.Code, "PAY-1", 2), default);

        await using var check = TestDatabase.Donations();
        Assert.Equal(owner, (await check.Donations.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveMatch>(message.Payload).OwnerId);
        Assert.Equal(DonationStatus.Requested, donation.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        await using var db = TestDatabase.Donations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new DonationService(db, new FixedCatalog(pool), new TestUser(null)).CreateAsync(new CreateDonationRequest(pool.Code, "PAY-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownPool_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Donations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new DonationService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateDonationRequest("NOPE", "PAY-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
