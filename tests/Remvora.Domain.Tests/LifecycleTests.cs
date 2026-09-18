using Remvora.Domain;
namespace Remvora.Domain.Tests;

public class LifecycleTests
{
    [Fact]
    public void ExternalCreationCannotActivate()
    {
        var device = new Device(Guid.NewGuid(), "kiosk");
        Assert.Equal(EnrollmentStatus.Created, device.EnrollmentStatus);
        Assert.Throws<DomainException>(() => device.Activate());
        Assert.Null(device.PublicKey);
    }
    [Fact]
    public void RevocationCannotBeUndoneByEnrollment()
    {
        var device = new Device(Guid.NewGuid(), "kiosk");
        device.RequestEnrollment("key", "Linux", "arm64", "0.1.0"); device.Activate(); device.Revoke();
        Assert.Throws<DomainException>(() => device.RequestEnrollment("new-key", "Linux", "arm64", "0.1.0"));
        Assert.Throws<DomainException>(() => device.Activate());
        Assert.Throws<DomainException>(() => device.Heartbeat());
    }
    [Fact] public void EmptyTenantIsRejected() => Assert.Throws<DomainException>(() => new Device(Guid.Empty, "device"));
    [Theory][InlineData("")][InlineData(" ")] public void BlankNamesAreRejected(string name) => Assert.Throws<DomainException>(() => new Device(Guid.NewGuid(), name));
}
