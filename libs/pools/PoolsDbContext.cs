using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Pledge.Pools;

public class PoolsDbContext(DbContextOptions<PoolsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "pools";

    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<MatchHold> Holds => Set<MatchHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Pool>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Pools_Remaining", "\"AvailableCents\" >= 0 AND \"AvailableCents\" <= \"OpeningCents\""));
        });

        b.Entity<MatchHold>(e =>
        {
            e.ToTable("MatchHolds");
            e.HasKey(x => x.DonationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Pool>().WithMany().HasForeignKey(x => x.PoolId);
        });
    }
}
