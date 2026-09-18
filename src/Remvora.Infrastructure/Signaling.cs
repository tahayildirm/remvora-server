using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Remvora.Application;
using Remvora.Domain;
namespace Remvora.Infrastructure;

public sealed record Signal(int ProtocolVersion, Guid MessageId, DateTimeOffset Timestamp, Guid? SessionId, string Type, JsonElement Payload)
{
    public static Signal Create(string type, Guid? session, object payload) => new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, session, type, JsonSerializer.SerializeToElement(payload, JsonOptions));
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
public sealed class Peer(WebSocket socket, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim sendLock = new(1, 1);
    public WebSocket Socket { get; } = socket;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long lastReceivedTicks = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcTicks;
    public DateTimeOffset LastReceivedAt => new(Interlocked.Read(ref lastReceivedTicks), TimeSpan.Zero);
    public bool IsAlive => Socket.State == WebSocketState.Open && clock.GetUtcNow() - LastReceivedAt < TimeSpan.FromSeconds(50);
    public async Task Send(Signal message, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try { using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5)); await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Signal.JsonOptions), WebSocketMessageType.Text, true, deadline.Token); }
        finally { sendLock.Release(); }
    }
    // A departed browser must not tear down the shared device control connection.
    public async Task<bool> ForwardToBrowser(Signal message, CancellationToken agentLifetime)
    {
        try { await Send(message, agentLifetime); return true; }
        catch (Exception ex) when (!agentLifetime.IsCancellationRequested &&
            ex is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            Socket.Abort();
            return false;
        }
    }
    public async Task<Signal> Receive(CancellationToken ct)
    {
        var buffer = new byte[65536]; var offset = 0; WebSocketReceiveResult result;
        do
        {
            if (offset == buffer.Length) throw new DomainException("SIGNAL_TOO_LARGE");
            result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), ct);
            if (result.MessageType != WebSocketMessageType.Text) throw new DomainException("SIGNAL_INVALID"); offset += result.Count;
        } while (!result.EndOfMessage);
        var message = JsonSerializer.Deserialize<Signal>(buffer.AsSpan(0, offset), Signal.JsonOptions) ?? throw new DomainException("SIGNAL_INVALID");
        if (message.ProtocolVersion != 1 || message.MessageId == Guid.Empty || Math.Abs((DateTimeOffset.UtcNow - message.Timestamp).TotalMinutes) > 2) throw new DomainException("SIGNAL_INVALID");
        Interlocked.Exchange(ref lastReceivedTicks, clock.GetUtcNow().UtcTicks);
        return message;
    }
}

/// <summary>Bounded single-node signaling. Every route is derived from a server-authorized session, never a peer-supplied device.</summary>
public sealed class SignalingHub(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<SecurityPolicy> policy)
{
    private readonly ConcurrentDictionary<Guid, bool> terminalPolicies = new();
    private readonly ConcurrentDictionary<Guid, Peer> agents = new();
    private readonly ConcurrentDictionary<Guid, (Guid Device, Peer Browser)> browsers = new();
    private readonly ConcurrentDictionary<Guid, (Guid Device, Guid Organization, DateTimeOffset Expires)> rebootRequests = new();
    public async Task<object> RequestReboot(Guid id, string confirmation, Database db, Actor actor, CancellationToken ct)
    {
        actor.Require("devices.reboot");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
        if (confirmation != device.DeviceCode) throw new DomainException("CONFIRMATION_REQUIRED");
        if (device.EnrollmentStatus != EnrollmentStatus.Active || !agents.TryGetValue(id, out var agent)) throw new DomainException("DEVICE_OFFLINE");
        foreach (var entry in rebootRequests.Where(x => x.Value.Expires < DateTimeOffset.UtcNow)) rebootRequests.TryRemove(entry.Key, out _);
        if (rebootRequests.Count >= 1024 || rebootRequests.Values.Any(x => x.Device == id)) throw new DomainException("COMMAND_PENDING");
        var command = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddSeconds(30);
        rebootRequests[command] = (id, actor.OrganizationId, expires);
        try
        {
            db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, id, "DEVICE_REBOOT_REQUESTED", actor.Ip, actor.UserAgent) { Metadata = JsonSerializer.Serialize(new { commandId = command }) });
            await db.SaveChangesAsync(ct);
            await agent.Send(Signal.Create("device.reboot", null, new { commandId = command, expiresAt = expires }), ct);
        }
        catch { rebootRequests.TryRemove(command, out _); throw; }
        return new { commandId = command, status = "requested", expiresAt = expires };
    }
    public void DisconnectDevice(Guid device)
    {
        if (agents.TryRemove(device, out var agent)) agent.Socket.Abort();
        foreach (var entry in browsers.Where(x => x.Value.Device == device))
            if (browsers.TryRemove(entry.Key, out var browser)) browser.Browser.Socket.Abort();
        foreach (var entry in rebootRequests.Where(x => x.Value.Device == device)) rebootRequests.TryRemove(entry.Key, out _);
    }
    public bool Online(Guid device) => agents.TryGetValue(device, out var peer) && peer.IsAlive;
    public object Presence(Guid device) => new
    {
        online = Online(device),
        busy = Online(device) && browsers.Values.Any(x => x.Device == device && x.Browser.Socket.State == WebSocketState.Open),
        lastSignalAt = agents.TryGetValue(device, out var peer) ? (DateTimeOffset?)peer.LastReceivedAt : null
    };
    public async Task<object> Authorize(SessionInput input, Database db, Actor actor, CancellationToken ct)
    {
        actor.Require(input.Kind == SessionKind.Terminal ? "devices.terminal" : "devices.remoteDesktop");
        if (!Enum.IsDefined(input.Kind) || actor.UserId is null || actor.IsApiKey) throw new DomainException("FORBIDDEN");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == input.DeviceId, ct) ?? throw new DomainException("NOT_FOUND");
        if (device.EnrollmentStatus != EnrollmentStatus.Active) throw new DomainException("DEVICE_NOT_ACTIVE");
        if (!Online(device.Id)) throw new DomainException("DEVICE_OFFLINE");
        var token = Tokens.New(); var session = new RemoteSession(actor.OrganizationId, actor.UserId.Value, device.Id, input.Kind, Tokens.Hash(token));
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(policy.Value.SessionTicketSeconds);
        db.RemoteSessions.Add(session); await db.SaveChangesAsync(ct);
        return new { sessionId = session.Id, token, expiresAt = session.ExpiresAt, kind = input.Kind.ToString() };
    }
    public async Task Agent(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(); var peer = new Peer(socket);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); var ct = lifetime.Token;
        Guid deviceId = Guid.Empty; Task? watchdog = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await peer.Receive(handshake.Token);
            if (hello.Type != "agent.authenticate" || hello.SessionId is not null) throw new DomainException("SIGNAL_INVALID");
            using var scope = scopes.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();
            var device = await service.Verify(hello.Payload.Deserialize<ProofInput>(Signal.JsonOptions) ?? throw new DomainException("PROOF_INVALID"), "connect", ct);
            deviceId = device.Id;
            if (!agents.TryAdd(deviceId, peer)) throw new DomainException("AGENT_ALREADY_CONNECTED");
            watchdog = WatchDevice(device.OrganizationId, deviceId, peer, lifetime);
            await peer.Send(Signal.Create("agent.connected", null, new { deviceId }), ct);
            var seen = new ReplayWindow(); var relayBudget = new RelayBudget(); var window = DateTimeOffset.UtcNow; var count = 0;
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(75));
                var signal = await peer.Receive(idle.Token);
                if (signal.Type == "relay.data") relayBudget.Check(signal.Payload, false); else CheckRate(ref window, ref count);
                if (signal.Type != "relay.data" && !seen.Accept(signal)) throw new DomainException("SIGNAL_REPLAY");
                if (signal.Type == "device.reboot.result" && signal.SessionId is null)
                {
                    var commandId = signal.Payload.GetProperty("commandId").GetGuid();
                    if (!rebootRequests.TryGetValue(commandId, out var request) || request.Device != deviceId || request.Expires < DateTimeOffset.UtcNow) throw new DomainException("SIGNAL_INVALID");
                    rebootRequests.TryRemove(commandId, out _);
                    var code = signal.Payload.GetProperty("code").GetString();
                    if (code is not ("accepted" or "denied" or "failed")) throw new DomainException("SIGNAL_INVALID");
                    using var resultScope = scopes.CreateScope(); resultScope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = request.Organization;
                    var resultDb = resultScope.ServiceProvider.GetRequiredService<Database>();
                    resultDb.Audit.Add(new AuditEvent(request.Organization, null, deviceId, "DEVICE_REBOOT_" + code.ToUpperInvariant(), null, null) { Metadata = JsonSerializer.Serialize(new { commandId }) });
                    await resultDb.SaveChangesAsync(ct); continue;
                }
                if (signal.Type == "agent.heartbeat" && signal.SessionId is null) continue;
                if (signal.SessionId is not Guid id) throw new DomainException("SIGNAL_INVALID");
                // In-flight output may arrive after a browser closed; never disconnect the whole agent for it.
                if (!browsers.TryGetValue(id, out var target)) continue;
                if (target.Device != deviceId) throw new DomainException("SIGNAL_INVALID");
                if (signal.Type is not ("session.accept" or "session.reject" or "webrtc.answer" or "webrtc.iceCandidate" or "session.close" or "relay.ready" or "relay.data")) throw new DomainException("SIGNAL_INVALID");
                if (signal.Type == "session.accept" && terminalPolicies.TryGetValue(id, out var requestedPolicy) &&
                    (!signal.Payload.TryGetProperty("terminalPolicyVersion", out var version) || !version.TryGetInt32(out var policyVersion) || policyVersion != 1 ||
                     !signal.Payload.TryGetProperty("terminalPrivilegeEscalation", out var applied) || applied.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || applied.GetBoolean() != requestedPolicy))
                {
                    await target.Browser.ForwardToBrowser(Signal.Create("session.reject", id, new { code = "TERMINAL_POLICY_AGENT_UPDATE_REQUIRED" }), ct);
                    await peer.Send(Signal.Create("session.close", id, new { }), ct);
                    target.Browser.Socket.Abort();
                    continue;
                }
                await target.Browser.ForwardToBrowser(signal, ct);
            }
        }
        catch (Exception ex) when (ex is DomainException or WebSocketException or OperationCanceledException or JsonException or DbUpdateConcurrencyException) { }
        finally
        {
            if (deviceId != Guid.Empty && agents.TryGetValue(deviceId, out var current) && ReferenceEquals(current, peer))
            {
                agents.TryRemove(deviceId, out _);
                foreach (var browser in browsers.Values.Where(x => x.Device == deviceId)) browser.Browser.Socket.Abort();
            }
            lifetime.Cancel(); socket.Abort(); if (watchdog is not null) await watchdog;
        }
    }
    public async Task Browser(HttpContext context, Database db, Actor actor)
    {
        if (!context.WebSockets.IsWebSocketRequest || actor.UserId is null) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(); var peer = new Peer(socket);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); lifetime.CancelAfter(TimeSpan.FromMinutes(policy.Value.RemoteSessionMinutes)); var ct = lifetime.Token;
        RemoteSession? session = null; Task? watchdog = null; Peer? agent = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await peer.Receive(handshake.Token);
            if (first.Type != "session.request" || first.SessionId is null) throw new DomainException("SIGNAL_INVALID");
            var token = first.Payload.GetProperty("token").GetString() ?? ""; var hash = Tokens.Hash(token);
            session = await db.RemoteSessions.SingleOrDefaultAsync(x => x.Id == first.SessionId && x.UserId == actor.UserId && x.TokenHash == hash && x.ConsumedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct) ?? throw new DomainException("SESSION_INVALID");
            actor.Require(session.Kind == SessionKind.Terminal ? "devices.terminal" : "devices.remoteDesktop");
            if (!agents.TryGetValue(session.DeviceId, out agent) || !await db.Devices.AnyAsync(x => x.Id == session.DeviceId && x.EnrollmentStatus == EnrollmentStatus.Active, ct)) throw new DomainException("DEVICE_OFFLINE");
            var terminalPolicy = session.Kind == SessionKind.Terminal && actor.Can("devices.terminalElevation") &&
                await db.Devices.AnyAsync(x => x.Id == session.DeviceId && x.AllowTerminalPrivilegeEscalation, ct);
            session.ConsumedAt = DateTimeOffset.UtcNow;
            db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, session.DeviceId, session.Kind == SessionKind.Terminal ? "TERMINAL_STARTED" : "REMOTE_DESKTOP_STARTED", actor.Ip, actor.UserAgent));
            await db.SaveChangesAsync(ct);
            if (!browsers.TryAdd(session.Id, (session.DeviceId, peer))) throw new DomainException("SESSION_INVALID");
            if (session.Kind == SessionKind.Terminal) terminalPolicies[session.Id] = terminalPolicy;
            watchdog = WatchUser(actor.OrganizationId, actor.UserId.Value, actor.SessionId, session.DeviceId, session.Kind, terminalPolicy, peer, lifetime);
            await agent.Send(Signal.Create("session.request", session.Id, new { kind = session.Kind.ToString(), allowTerminalPrivilegeEscalation = terminalPolicy, expiresAt = DateTimeOffset.UtcNow.AddMinutes(policy.Value.RemoteSessionMinutes) }), ct);
            var seen = new ReplayWindow(); var relayBudget = new RelayBudget(); var window = DateTimeOffset.UtcNow; var count = 0;
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(40));
                var message = await peer.Receive(idle.Token);
                if (message.Type == "relay.data") relayBudget.Check(message.Payload, true); else CheckRate(ref window, ref count);
                if ((message.Type != "relay.data" && !seen.Accept(message)) || message.SessionId != session.Id) throw new DomainException("SIGNAL_INVALID");
                if (message.Type == "session.heartbeat") continue;
                if (message.Type is not ("webrtc.offer" or "webrtc.iceCandidate" or "session.close" or "relay.start" or "relay.data")) throw new DomainException("SIGNAL_INVALID");
                await agent.Send(message, ct); if (message.Type == "session.close") break;
            }
        }
        catch (Exception ex) when (ex is DomainException or WebSocketException or OperationCanceledException or JsonException or DbUpdateConcurrencyException or InvalidOperationException) { }
        finally
        {
            lifetime.Cancel(); socket.Abort(); if (watchdog is not null) await watchdog;
            if (session is not null) terminalPolicies.TryRemove(session.Id, out _);
            if (session is not null && browsers.TryRemove(session.Id, out _))
            {
                if (agent is not null) { try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await agent.Send(Signal.Create("session.close", session.Id, new { }), timeout.Token); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { } }
                using var scope = scopes.CreateScope(); scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = actor.OrganizationId;
                var endDb = scope.ServiceProvider.GetRequiredService<Database>(); var persisted = await endDb.RemoteSessions.SingleOrDefaultAsync(x => x.Id == session.Id);
                if (persisted is not null) persisted.EndedAt = DateTimeOffset.UtcNow; endDb.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, session.DeviceId, session.Kind == SessionKind.Terminal ? "TERMINAL_ENDED" : "REMOTE_DESKTOP_ENDED", actor.Ip, actor.UserAgent)); await endDb.SaveChangesAsync();
            }
        }
    }
    private static void CheckRate(ref DateTimeOffset window, ref int count)
    {
        if ((DateTimeOffset.UtcNow - window).TotalSeconds >= 60) { window = DateTimeOffset.UtcNow; count = 0; }
        if (++count > 240) throw new DomainException("SIGNAL_RATE_LIMIT");
    }
    private async Task WatchDevice(Guid org, Guid device, Peer peer, CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                using var scope = scopes.CreateScope(); scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = org;
                var db = scope.ServiceProvider.GetRequiredService<Database>(); var entity = await db.Devices.SingleOrDefaultAsync(x => x.Id == device, lifetime.Token);
                if (entity is null || entity.EnrollmentStatus != EnrollmentStatus.Active || !peer.IsAlive) break;
                if (entity.LastSeenAt is null || peer.LastReceivedAt > entity.LastSeenAt) { entity.Heartbeat(); await db.SaveChangesAsync(lifetime.Token); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or DbUpdateException) { }
        finally { lifetime.Cancel(); peer.Socket.Abort(); }
    }
    private async Task WatchUser(Guid org, Guid user, Guid? userSession, Guid deviceId, SessionKind kind, bool terminalPolicy, Peer peer, CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                using var scope = scopes.CreateScope(); scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = org;
                var db = scope.ServiceProvider.GetRequiredService<Database>();
                var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user, lifetime.Token);
                var requestActor = scope.ServiceProvider.GetRequiredService<Actor>();
                requestActor.DeviceGroups = account?.DeviceGroupScope is null ? null : System.Text.Json.JsonSerializer.Deserialize<Guid[]>(account.DeviceGroupScope) ?? [];
                var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, lifetime.Token);
                if (device is null) break;
                if (kind == SessionKind.Terminal && account is not null)
                {
                    var permissions = await UserAdministration.ResolvePermissions(db, account.Role, lifetime.Token);
                    if (terminalPolicy != (device.AllowTerminalPrivilegeEscalation && permissions.Contains("devices.terminalElevation"))) break;
                }
                if (account is null || !(await UserAdministration.ResolvePermissions(db, account.Role, lifetime.Token)).Contains(kind == SessionKind.Terminal ? "devices.terminal" : "devices.remoteDesktop") || !await db.Sessions.AnyAsync(x => x.Id == userSession && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, lifetime.Token)) break;
            }
        }
        catch (OperationCanceledException) { }
        finally { lifetime.Cancel(); peer.Socket.Abort(); }
    }
}

/// <summary>Retain IDs until their signed protocol timestamp becomes stale, including future clock skew.</summary>
public sealed class ReplayWindow
{
    private readonly Dictionary<Guid, DateTimeOffset> entries = new();
    public bool Accept(Signal message)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in entries.Where(x => x.Value < now).Select(x => x.Key).ToArray()) entries.Remove(id);
        return entries.Count < 1024 && entries.TryAdd(message.MessageId, message.Timestamp.AddMinutes(2));
    }
}
