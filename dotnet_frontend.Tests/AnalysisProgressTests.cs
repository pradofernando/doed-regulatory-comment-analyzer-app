using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AnalysisProgressTests
{
    [Fact]
    public void Percent_IsZero_WhenTotalZero()
    {
        var p = new AnalysisProgress { Current = 5, Total = 0 };
        Assert.Equal(0, p.Percent);
    }

    [Fact]
    public void Percent_ComputesRatio()
    {
        var p = new AnalysisProgress { Current = 1, Total = 4 };
        Assert.Equal(25, p.Percent, precision: 3);
    }

    [Fact]
    public void Percent_ClampsAbove100()
    {
        var p = new AnalysisProgress { Current = 10, Total = 4 };
        Assert.Equal(100, p.Percent, precision: 3);
    }
}
