using AegisScan.KubeOps.Api;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class CveEndpointsTests
{
    [Theory]
    [InlineData("openssl 3.0.1 — Upgrade openssl to version 3.0.7", "openssl", "3.0.1", "Upgrade openssl to version 3.0.7")]
    [InlineData("@scope/pkg 1.2.3", "@scope/pkg", "1.2.3", "")]
    [InlineData("lonely", "lonely", "", "")]
    public void SplitsFindingDetail(string detail, string package, string version, string fix) =>
        Assert.Equal((package, version, fix), CveEndpoints.SplitDetail(detail));
}
