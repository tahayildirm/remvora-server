using System.Net.WebSockets;
using Remvora.Infrastructure;
namespace Remvora.Api.IntegrationTests;
public class PeerForwardingTests
{
    [Fact]
    public async Task FailedBrowserDoesNotPreventForwardingToAnotherSession()
    {
        using var broken = new TestSocket(true);
        using var healthy = new TestSocket(false);
        var signal = Signal.Create("relay.data", Guid.NewGuid(), new { });
        Assert.False(await new Peer(broken).ForwardToBrowser(signal, CancellationToken.None));
        Assert.True(broken.Aborted);
        Assert.True(await new Peer(healthy).ForwardToBrowser(signal, CancellationToken.None));
        Assert.Equal(1, healthy.Sent);
    }
    [Fact]
    public async Task AgentShutdownCancellationStillPropagates()
    {
        using var socket = new TestSocket(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Peer(socket)
            .ForwardToBrowser(Signal.Create("session.close", Guid.NewGuid(), new { }), cancelled.Token));
        Assert.False(socket.Aborted);
    }
    [Fact]
    public void PresenceExpiresWithoutRealIncomingTraffic()
    {
        using var socket = new TestSocket(false); var clock = new ManualClock(); var peer = new Peer(socket, clock);
        Assert.True(peer.IsAlive); clock.Now = clock.Now.AddSeconds(51); Assert.False(peer.IsAlive);
        using var live = new TestSocket(false); var connected = new Peer(live); Assert.True(connected.IsAlive);
        live.Abort(); Assert.False(connected.IsAlive);
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TestSocket(bool fail) : WebSocket
    {
        public bool Aborted { get; private set; }
        public int Sent { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (fail) throw new WebSocketException("Browser disconnected");
            Sent++;
            return Task.CompletedTask;
        }
    }
}
