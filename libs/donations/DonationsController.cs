using System.Text.Json;
using Pledge.Shared.Auth;
using Pledge.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Pledge.Donations;

[ApiController]
[Route("api/donations")]
[Authorize]
public class DonationsController(DonationService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the donation for the signed-in user. Pools and the receipt follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<DonationDto>> Create(CreateDonationRequest request, CancellationToken ct)
    {
        var donation = await service.CreateAsync(request, ct);
        return Accepted($"/api/donations/{donation.Id}", DonationDto.From(donation));
    }

    /// <summary>Your donations, newest first, a page at a time (default 100, at most 100). Staff see everyone's. The total is in X-Total-Count.</summary>
    [HttpGet]
    public async Task<IEnumerable<DonationDto>> List([FromQuery] string? poolCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var (items, total) = await service.ListAsync(poolCode, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return items.Select(DonationDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<DonationDto> Get(Guid id, CancellationToken ct) => DonationDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/refund")]
    public async Task<ActionResult<DonationDto>> Refund(Guid id, CancellationToken ct) =>
        Accepted($"/api/donations/{id}", DonationDto.From(await service.RefundAsync(id, ct)));

    /// <summary>The Kafka event log for one donation, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
