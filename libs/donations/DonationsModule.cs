using Pledge.Shared;
using Pledge.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pledge.Donations;

public static class DonationsModule
{
    public static IServiceCollection AddDonationsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Pledge") ?? throw new InvalidOperationException("Connection string 'Pledge' is not configured.");
        services.AddDbContext<DonationsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DonationsDbContext.SchemaName)));
        services.Configure<DonationOptions>(config.GetSection("Donations"));
        services.AddScoped<DonationService>();
        services.AddScoped<DonationsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<DonationsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<DonationsDbContext>>("donations-db", tags: ["ready"]);

        messaging.AddOutbox<DonationsDbContext>()
            .AddConsumer<DonationsHandler>(Topology.DonationsQueue,
                [Topology.HeldKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
