using Remvora.Domain;
namespace Remvora.Application;

/// <summary>Request-local identity, resolved only from verified credentials.</summary>
public sealed class Actor
{
    public Guid OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public HashSet<string> Permissions { get; set; } = [];
    public string Role { get; set; } = "";
    public bool IsApiKey { get; set; }
    public Guid[]? DeviceGroups { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public bool Can(string permission) => OrganizationId != Guid.Empty && Permissions.Contains(permission) &&
        (DeviceGroups is null || ((permission.StartsWith("devices.", StringComparison.Ordinal) && permission != "devices.create") || permission == "enrollment.manage"));
    public void Require(string permission)
    {
        if (!Can(permission)) throw new DomainException("FORBIDDEN");
    }
}
public static class Permissions
{
    public static readonly string[] All = ["devices.view", "devices.create", "devices.edit", "devices.delete", "devices.remoteDesktop", "devices.terminal", "devices.reboot", "devices.terminalPolicy", "devices.terminalElevation", "groups.manage", "contacts.manage", "locations.manage", "users.manage", "apiKeys.manage", "audit.view", "enrollment.manage"];
    public static HashSet<string> ForRole(string role) => role switch
    {
        "Owner" or "Admin" => [.. All],
        "Operator" => ["devices.view", "devices.terminal", "devices.remoteDesktop"],
        "Viewer" => ["devices.view"],
        _ => []
    };
    public static string? ForScope(string scope) => scope switch
    { "devices.read" => "devices.view", "devices.create" => "devices.create", "devices.update" => "devices.edit", "devices.delete" => "devices.delete", _ => null };
}
public sealed record DeviceInput(string Name, string? Description);
public sealed record LoginInput(Guid? OrganizationId, string Email, string Password, string? Code);
public sealed record EnrollmentInput(string Token, string PublicKey, string OperatingSystem, string Architecture, string AgentVersion);
public sealed record ProofInput(Guid ChallengeId, string Signature);
public sealed record ApiKeyInput(string Name, string[] Scopes, DateTimeOffset ExpiresAt);
public sealed record SessionInput(Guid DeviceId, SessionKind Kind);

/// <summary>Application boundary for device operations, independent of EF and HTTP.</summary>
public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> List(CancellationToken ct, int offset = 0, int limit = 100);
    Task<Device?> Find(Guid id, CancellationToken ct);
    Task Save(Device device, bool isNew, string eventType, CancellationToken ct);
    Task Delete(Device device, CancellationToken ct);
}
public sealed class DeviceService(IDeviceRepository repository, Actor actor)
{
    public async Task<IReadOnlyList<Device>> List(CancellationToken ct, int offset = 0, int limit = 100) { actor.Require("devices.view"); var page = new PageRequest(offset, limit); return await repository.List(ct, page.Offset, page.Limit); }
    public async Task<Device> Get(Guid id, CancellationToken ct)
    { actor.Require("devices.view"); return await repository.Find(id, ct) ?? throw new DomainException("NOT_FOUND"); }
    public async Task<Device> Create(DeviceInput input, CancellationToken ct)
    {
        actor.Require("devices.create"); var device = new Device(actor.OrganizationId, input.Name); device.Rename(input.Name, input.Description);
        await repository.Save(device, true, "DEVICE_CREATED", ct); return device;
    }
    public async Task<Device> Update(Guid id, DeviceInput input, CancellationToken ct)
    {
        actor.Require("devices.edit"); var device = await repository.Find(id, ct) ?? throw new DomainException("NOT_FOUND");
        device.Rename(input.Name, input.Description); await repository.Save(device, false, "DEVICE_UPDATED", ct); return device;
    }
    public async Task SetTerminalPolicy(Guid id, bool allowed, CancellationToken ct)
    {
        actor.Require("devices.terminalPolicy");
        var device = await repository.Find(id, ct) ?? throw new DomainException("NOT_FOUND");
        if (allowed && !string.Equals(device.OperatingSystem, "linux", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("TERMINAL_POLICY_LINUX_ONLY");
        device.SetTerminalPrivilegeEscalation(allowed);
        await repository.Save(device, false, allowed ? "TERMINAL_ELEVATION_ALLOWED" : "TERMINAL_ELEVATION_BLOCKED", ct);
    }
    public async Task SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        actor.Require("devices.edit"); var device = await repository.Find(id, ct) ?? throw new DomainException("NOT_FOUND");
        if (enabled) device.Enable(); else device.Disable();
        await repository.Save(device, false, enabled ? "DEVICE_ENABLED" : "DEVICE_DISABLED", ct);
    }
    public async Task DeletePermanently(Guid id, string confirmation, CancellationToken ct)
    {
        actor.Require("devices.delete");
        var device = await repository.Find(id, ct) ?? throw new DomainException("NOT_FOUND");
        if (confirmation != device.DeviceCode) throw new DomainException("CONFIRMATION_REQUIRED");
        await repository.Delete(device, ct);
    }
    public async Task Revoke(Guid id, CancellationToken ct)
    {
        actor.Require("devices.delete"); var device = await repository.Find(id, ct) ?? throw new DomainException("NOT_FOUND");
        device.Revoke(); await repository.Save(device, false, "DEVICE_REVOKED", ct);
    }
}

/// <summary>Bounded offset pagination; deterministic ordering is supplied by each repository.</summary>
public sealed record PageRequest
{
    public int Offset { get; }
    public int Limit { get; }
    public PageRequest(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 500) throw new DomainException("PAGINATION_INVALID");
        Offset = offset; Limit = limit;
    }
}

public sealed record ProfileInput(string Email, string CurrentPassword, string? NewPassword);
public sealed record PasswordInput(string Password);

public sealed record PermanentDeleteInput(string Confirmation);
