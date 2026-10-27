namespace Pledge.Shared.Contracts;

public record PoolInfo(Guid Id, string Code);

/// <summary>
/// What Donations may ask Pools, without referencing it. Pools implements it over its own tables;
/// Donations never reads another module's schema.
/// </summary>
public interface IPoolCatalog
{
    Task<PoolInfo?> FindAsync(string code, CancellationToken ct);
}
