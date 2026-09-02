using HD.Acs.Vda5050;
using Xunit;

namespace HD.Acs.Core.Tests;

public sealed class Vda5050TopicsTests
{
    [Fact]
    public void AcsConnection_UsesApprovedN12Identity()
    {
        Assert.Equal("HD_ACS", Vda5050Topics.AcsIdentity.Manufacturer);
        Assert.Equal("hd-acs-master", Vda5050Topics.AcsIdentity.SerialNumber);
        Assert.Equal("uagv/v2/HD_ACS/hd-acs-master/connection", Vda5050Topics.AcsConnection());
    }

    [Fact]
    public void AcsConnection_AllowsConfiguredPrefixAndVersion()
    {
        Assert.Equal("test/v3/HD_ACS/hd-acs-master/connection",
            Vda5050Topics.AcsConnection("test", "v3"));
    }
}
