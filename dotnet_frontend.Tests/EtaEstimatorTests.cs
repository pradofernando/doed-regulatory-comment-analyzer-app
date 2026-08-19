using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class EtaEstimatorTests
{
    [Theory]
    [InlineData(0, 10)]   // nothing done yet
    [InlineData(10, 10)]  // already complete
    [InlineData(11, 10)]  // overshoot
    [InlineData(5, 0)]    // bad total
    public void Estimate_ReturnsNull_WhenNotMeaningful(int completed, int total)
    {
        var eta = EtaEstimator.Estimate(TimeSpan.FromSeconds(30), completed, total);
        Assert.Null(eta);
    }

    [Fact]
    public void Estimate_ReturnsNull_WhenElapsedNotPositive()
    {
        Assert.Null(EtaEstimator.Estimate(TimeSpan.Zero, 2, 10));
        Assert.Null(EtaEstimator.Estimate(TimeSpan.FromSeconds(-5), 2, 10));
    }

    [Fact]
    public void Estimate_LinearProjection()
    {
        // 10s elapsed for 2 of 10 => 5s/item, 8 remaining => 40s.
        var eta = EtaEstimator.Estimate(TimeSpan.FromSeconds(10), 2, 10);
        Assert.NotNull(eta);
        Assert.Equal(40, eta!.Value.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(65, "1m 05s")]
    [InlineData(125, "2m 05s")]
    [InlineData(3660, "1h 01m")]
    public void Format_ProducesCompactString(int seconds, string expected)
    {
        Assert.Equal(expected, EtaEstimator.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Format_NegativeClampsToZero()
    {
        Assert.Equal("0s", EtaEstimator.Format(TimeSpan.FromSeconds(-10)));
    }
}
