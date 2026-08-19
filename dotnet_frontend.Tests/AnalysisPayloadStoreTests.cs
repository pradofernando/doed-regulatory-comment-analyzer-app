using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AnalysisPayloadStoreTests
{
    [Fact]
    public void GzipCodec_RoundTrip_PreservesCategorizationPayload()
    {
        var payload = new AnalysisRunPayload
        {
            Categorizations =
            [
                new CategorizationPayload(1, "{\"sentiment\":\"supportive\"}", "{\"sentiment\":\"supportive\"}"),
                new CategorizationPayload(2, "raw prose", "{}"),
            ],
        };

        var compressed = AnalysisPayloadCodec.Serialize(payload);
        var loaded = AnalysisPayloadCodec.Deserialize(compressed);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(payload.Categorizations, loaded.Categorizations);
        Assert.True(compressed.Length < 256);
    }
}