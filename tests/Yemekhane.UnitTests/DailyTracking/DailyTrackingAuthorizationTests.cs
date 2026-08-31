using Yemekhane.Api.Authorization;
using Yemekhane.Api.Controllers;

namespace Yemekhane.UnitTests.DailyTracking;

public sealed class DailyTrackingAuthorizationTests
{
    [Fact]
    public void EndpointRequiresAccessReadPermission()
    {
        var attribute = Assert.Single(typeof(DailyTrackingController)
            .GetCustomAttributes(typeof(PermissionAuthorizeAttribute), true).Cast<PermissionAuthorizeAttribute>());
        Assert.Equal(Permissions.Policy(Permissions.AccessRead), attribute.Policy);
    }
}
