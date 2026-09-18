using System.Text.Json;
using Remvora.Infrastructure;
using Remvora.Domain;
namespace Remvora.Api.IntegrationTests;

public class RelayBudgetTests
{
    private static JsonElement Packet(long sequence, string channel = "input", int part = 0, bool last = true, int size = 8) => JsonSerializer.SerializeToElement(new { sequence, channel, part, last, data = new string('A', size) });
    [Fact]
    public void RejectsReplayBrowserMediaAndFragmentedControl()
    {
        var budget = new RelayBudget(); budget.Check(Packet(1), true);
        Assert.Throws<DomainException>(() => budget.Check(Packet(1), true));
        Assert.Throws<DomainException>(() => budget.Check(Packet(2, "video"), true));
        Assert.Throws<DomainException>(() => budget.Check(Packet(2, part: 1), true));
        Assert.Throws<DomainException>(() => budget.Check(Packet(2, last: false), true));
        Assert.Throws<DomainException>(() => budget.Check(Packet(2, size: 44001), false));
        budget.Check(Packet(2, "video"), false);
    }
    [Fact]
    public void BoundedMediaBurstDoesNotUseSignalingReplayCache()
    {
        var budget = new RelayBudget();
        for (var i = 0; i < 100; i++) budget.Check(Packet(i, "audio"), false);
    }
}
