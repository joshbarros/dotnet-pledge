using Pledge.Donations;
using Xunit;

namespace Pledge.UnitTests;

public class DonationRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("PAY-1", 0)]
    [InlineData("PAY-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long amountCents) =>
        Assert.NotNull(DonationRules.Validate(reference, amountCents));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(DonationRules.Validate("PAY-1", DonationRules.MaxDonationCents));
        Assert.NotNull(DonationRules.Validate("PAY-1", DonationRules.MaxDonationCents + 1));
        Assert.NotNull(DonationRules.Validate(new string('x', DonationRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)]
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long amountCents) =>
        Assert.NotNull(DonationRules.Validate("PAY-1", amountCents));

    [Fact]
    public void FeeArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => DonationRules.FeeCents(long.MaxValue));
        Assert.Throws<OverflowException>(() => DonationRules.FeeCents(long.MaxValue / DonationRules.FeeBasisPoints + 1));
    }

    [Fact]
    public void HappyFee_IsFlatFeePlusHalfAPercent()
    {
        Assert.Null(DonationRules.Validate("PAY-1", 100_000));
        Assert.Equal(50 + 500, DonationRules.FeeCents(100_000)); // $1,000.00 -> $5.50
        Assert.Equal(50, DonationRules.FeeCents(1)); // the percentage truncates to zero
        Assert.Equal(50 + 25_000, DonationRules.FeeCents(DonationRules.MaxDonationCents));
    }
}
