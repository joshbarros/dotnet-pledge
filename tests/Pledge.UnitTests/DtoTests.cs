using Pledge.Donations;
using Pledge.Pools;
using Xunit;

namespace Pledge.UnitTests;

public class DtoTests
{
    [Fact]
    public void DonationDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var donation = new Donation
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            PoolId = Guid.NewGuid(),
            PoolCode = "S-1",
            Reference = "PAY-1",
            AmountCents = 3,
            FeeCents = 277_500,
            Status = DonationStatus.Completed,
            ReceiptNumber = "RC-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = DonationDto.From(donation);

        Assert.Equal(donation.Id, dto.Id);
        Assert.Equal(donation.OwnerId, dto.OwnerId);
        Assert.Equal("Completed", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("RC-1", dto.ReceiptNumber);
    }

    [Fact]
    public void PoolDto_MapsTheScarceNumber()
    {
        var pool = new Pool { Id = Guid.NewGuid(), Code = "S-1", Sponsor = "v", Currency = "o", Cause = "d", OpeningCents = 10, AvailableCents = 4 };
        var dto = PoolDto.From(pool);
        Assert.Equal(10, dto.OpeningCents);
        Assert.Equal(4, dto.AvailableCents);
    }
}
