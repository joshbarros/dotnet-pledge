using Pledge.Donations;
using Pledge.Pools;
using Pledge.Receipts;
using Pledge.Shared.Auth;
using Pledge.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Pledge.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("PLEDGE_TEST_DB")
        ?? "Host=localhost;Port=5482;Database=pledge_test;Username=pledge;Password=pledge";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static DonationsDbContext Donations() => new(Options<DonationsDbContext>(DonationsDbContext.SchemaName));
    public static PoolsDbContext Pools() => new(Options<PoolsDbContext>(PoolsDbContext.SchemaName));
    public static ReceiptsDbContext Receipts() => new(Options<ReceiptsDbContext>(ReceiptsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var donations = Donations();
        await using var pools = Pools();
        await using var receipts = Receipts();
        await donations.Database.MigrateAsync();
        await pools.Database.MigrateAsync();
        await receipts.Database.MigrateAsync();
        await donations.Database.ExecuteSqlRawAsync(
            "TRUNCATE donations.\"InboxMessages\", donations.\"OutboxMessages\", donations.\"Donations\", " +
            "pools.\"InboxMessages\", pools.\"OutboxMessages\", pools.\"MatchHolds\", pools.\"Pools\", " +
            "receipts.\"InboxMessages\", receipts.\"OutboxMessages\", receipts.\"Receipts\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Pool> AddPoolAsync(long openingCents)
    {
        await using var db = Pools();
        var pool = new Pool
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Sponsor = "MV Test",
            Currency = "USD",
            Cause = "Main Cause",
            OpenedAt = DateTimeOffset.UtcNow.AddDays(7),
            OpeningCents = openingCents,
            AvailableCents = openingCents,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Pools.Add(pool);
        await db.SaveChangesAsync();
        return pool;
    }

    public static async Task<Donation> AddDonationAsync(Pool pool, string reference, long amountCents, DonationStatus status, Guid? ownerId = null)
    {
        await using var db = Donations();
        var donation = new Donation
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            PoolId = pool.Id,
            PoolCode = pool.Code,
            Reference = reference,
            AmountCents = amountCents,
            FeeCents = DonationRules.FeeCents(amountCents),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Donations.Add(donation);
        await db.SaveChangesAsync();
        return donation;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Pool[] pools) : IPoolCatalog
{
    public Task<PoolInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(pools.Where(s => s.Code == code).Select(s => new PoolInfo(s.Id, s.Code)).FirstOrDefault());
}
