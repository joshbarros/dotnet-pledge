using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Pledge.Donations;

public class DonationsDbContext(DbContextOptions<DonationsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "donations";

    public DbSet<Donation> Donations => Set<Donation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Donation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(DonationRules.MaxReferenceLength);
            e.HasIndex(x => new { x.PoolId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
