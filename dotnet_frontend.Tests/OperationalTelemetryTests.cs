using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class OperationalTelemetryTests
{
    [Fact]
    public void EstimateCostUsd_UsesConfiguredInputAndOutputRates()
    {
        var options = new FoundryCostOptions
        {
            InputUsdPerMillionTokens = 2m,
            OutputUsdPerMillionTokens = 8m,
        };

        var cost = OperationalTelemetry.EstimateCostUsd(500_000, 250_000, options);

        Assert.Equal(3m, cost);
    }
}