using Yemekhane.Api.Authorization;
using Yemekhane.Api.Controllers;

namespace Yemekhane.UnitTests.Dashboard;

public sealed class DashboardAuthorizationTests
{
    [Fact]
    public void EndpointRequiresDedicatedDashboardPermission()
    {
        var attribute = Assert.Single(typeof(DashboardController).GetCustomAttributes(typeof(PermissionAuthorizeAttribute), true).Cast<PermissionAuthorizeAttribute>());
        Assert.Equal(Permissions.Policy(Permissions.DashboardRead), attribute.Policy);
    }
}
