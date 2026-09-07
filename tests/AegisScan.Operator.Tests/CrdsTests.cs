using System.Reflection;
using k8s.Models;
using Xunit;

namespace AegisScan.KubeOps.Tests;

public sealed class CrdsTests
{
    [Theory]
    [InlineData("AegisScan.KubeOps.Resources.V1Repository", "Repository", "repositories")]
    [InlineData("AegisScan.KubeOps.Resources.V1RepositorySource", "RepositorySource", "repositorysources")]
    [InlineData("AegisScan.KubeOps.Resources.V1ScanPolicy", "ScanPolicy", "scanpolicies")]
    public void AllCustomResourcesHaveKubernetesIdentity(string typeName, string kind, string pluralName)
    {
        var type = Assembly.Load("AegisScan.Operator").GetType(typeName)!;
        var identity = type.GetCustomAttribute<KubernetesEntityAttribute>();
        Assert.NotNull(identity);
        Assert.Equal("aegisscan.io", identity!.Group);
        Assert.Equal(kind, identity.Kind);
        Assert.Equal("v1alpha1", identity.ApiVersion);
        Assert.Equal(pluralName, identity.PluralName);
    }
}
