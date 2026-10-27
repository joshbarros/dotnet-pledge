using Pledge.Shared;
using Pledge.Shared.Auth;
using Pledge.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Pledge.Pools;

public record CreatePoolRequest(string? Code, string? Sponsor, string? Currency, string? Cause, DateTimeOffset? OpenedAt, long OpeningCents);

public record PoolDto(Guid Id, string Code, string Sponsor, string Currency, string Cause, DateTimeOffset OpenedAt, long OpeningCents, long AvailableCents)
{
    public static PoolDto From(Pool s) => new(s.Id, s.Code, s.Sponsor, s.Currency, s.Cause, s.OpenedAt, s.OpeningCents, s.AvailableCents);
}

/// <summary>What other modules may ask Pools, over Pools's own tables.</summary>
public sealed class PoolCatalog(PoolsDbContext db) : IPoolCatalog
{
    public async Task<PoolInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Pools.AsNoTracking().Where(s => s.Code == code).Select(s => new PoolInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/pools")]
[Authorize]
public class PoolsController(PoolsDbContext db) : ControllerBase
{
    private const long MaxOpeningCents = 10_000_000_000; // $100,000,000.00

    [HttpGet]
    public async Task<IEnumerable<PoolDto>> List(CancellationToken ct) =>
        (await db.Pools.AsNoTracking().OrderBy(s => s.OpenedAt).ThenBy(s => s.Code).ToListAsync(ct)).Select(PoolDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<PoolDto>> Get(string code, CancellationToken ct)
    {
        var pool = await db.Pools.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return pool is null ? NotFound() : PoolDto.From(pool);
    }

    /// <summary>Opens a pool. Staff only. This is the only place AvailableCents is initialised; after that only the Pools handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<PoolDto>> Create(CreatePoolRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Sponsor) || string.IsNullOrWhiteSpace(request.Currency) || string.IsNullOrWhiteSpace(request.Cause))
            throw new DomainException(400, "Sponsor, currency and cause are required.");
        if (request.OpeningCents < 1 || request.OpeningCents > MaxOpeningCents)
            throw new DomainException(400, $"Opening balance must be between 1 and {MaxOpeningCents} cents.");
        if (await db.Pools.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Pool '{code}' already exists.");

        var pool = new Pool
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Sponsor = request.Sponsor.Trim(),
            Currency = request.Currency.Trim(),
            Cause = request.Cause.Trim(),
            OpenedAt = request.OpenedAt ?? DateTimeOffset.UtcNow.AddDays(14),
            OpeningCents = request.OpeningCents,
            AvailableCents = request.OpeningCents,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Pools.Add(pool);
        await db.SaveChangesAsync(ct);
        return Created($"/api/pools/{pool.Code}", PoolDto.From(pool));
    }
}
