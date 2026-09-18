using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Remvora.Application;
using Remvora.Domain;
namespace Remvora.Infrastructure;

public sealed record UserInput(string Email, string Password, string Role);
public sealed record UserUpdateInput(string Email, string? NewPassword, bool Unlock = false, bool RevokeSessions = false);
public sealed record RoleInput(string Name, string[] Permissions);
public sealed class UserAdministration(Database db, Actor actor, IPasswordHasher<User> hasher)
{
    public static async Task<HashSet<string>> ResolvePermissions(Database db, string role, CancellationToken ct)
    {
        var builtIn = Permissions.ForRole(role); if (builtIn.Count > 0) return builtIn;
        var custom = await db.Set<RoleDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Name == role, ct);
        return custom is null ? [] : [.. custom.Permissions];
    }
    public async Task<object> Users(CancellationToken ct, int offset = 0, int limit = 100) { actor.Require("users.manage"); var page = new PageRequest(offset, limit); return await db.Users.AsNoTracking().OrderBy(x => x.Email).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).Select(x => new { x.Id, x.OrganizationId, x.Email, x.Role, x.TwoFactorEnabled, x.FailedAttempts, x.LockedUntil, x.DeviceGroupScope, ActiveSessions = db.Sessions.Count(session => session.UserId == x.Id && session.RevokedAt == null && session.ExpiresAt > DateTimeOffset.UtcNow) }).ToListAsync(ct); }
    public async Task<object> Roles(CancellationToken ct)
    {
        actor.Require("users.manage");
        return new { builtIn = new[] { "Owner", "Admin", "Operator", "Viewer" }.Select(name => new { name, permissions = Permissions.ForRole(name) }), custom = await db.Set<RoleDefinition>().AsNoTracking().ToListAsync(ct), permissions = Permissions.All };
    }
    public async Task<object> Create(UserInput input, CancellationToken ct)
    {
        actor.Require("users.manage");
        if (input.Password.Length is < 8 or > 256 || input.Email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(input.Email, out _)) throw new DomainException("INPUT_INVALID");
        await ValidateRole(input.Role, ct); var user = new User(actor.OrganizationId, input.Email.Trim().ToUpperInvariant(), "") { Role = input.Role };
        user.PasswordHash = hasher.HashPassword(user, input.Password); db.Users.Add(user); Audit("USER_CREATED"); await db.SaveChangesAsync(ct); return new { user.Id, user.Email, user.Role };
    }
    public async Task Update(Guid id, UserUpdateInput input, CancellationToken ct)
    {
        actor.Require("users.manage");
        if (actor.Role != "Owner" || actor.IsApiKey) throw new DomainException("FORBIDDEN");
        var email = input.Email?.Trim().ToUpperInvariant() ?? "";
        if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out _) ||
            (input.NewPassword is not null && input.NewPassword.Length is < 8 or > 256)) throw new DomainException("INPUT_INVALID");
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
        if (await db.Users.AnyAsync(x => x.Id != id && x.Email == email, ct)) throw new DomainException("EMAIL_IN_USE");
        var credentialsChanged = user.Email != email || input.NewPassword is not null;
        user.Email = email;
        if (input.NewPassword is not null) user.PasswordHash = hasher.HashPassword(user, input.NewPassword);
        if (input.Unlock) { user.LockedUntil = null; user.FailedAttempts = 0; }
        if (credentialsChanged || input.RevokeSessions)
            foreach (var session in await db.Sessions.Where(x => x.UserId == id && x.RevokedAt == null).ToListAsync(ct)) session.RevokedAt = DateTimeOffset.UtcNow;
        db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, null, "USER_UPDATED", actor.Ip, actor.UserAgent) { Metadata = System.Text.Json.JsonSerializer.Serialize(new { userId = id, credentialsChanged, input.Unlock, input.RevokeSessions }) });
        await db.SaveChangesAsync(ct);
    }
    public async Task ChangeRole(Guid id, string role, CancellationToken ct)
    {
        actor.Require("users.manage"); await ValidateRole(role, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
        if (user.Role == "Owner")
        {
            if (actor.Role != "Owner") throw new DomainException("FORBIDDEN");
            if (role != "Owner" && await db.Users.CountAsync(x => x.Role == "Owner", ct) <= 1) throw new DomainException("LAST_OWNER_REQUIRED");
        }
        user.Role = role; if (role == "Owner") user.DeviceGroupScope = null; Audit("PERMISSION_CHANGED"); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
    public async Task CreateRole(RoleInput input, CancellationToken ct)
    {
        actor.Require("users.manage");
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 64 || Permissions.ForRole(input.Name).Count > 0 || input.Permissions.Length == 0 || input.Permissions.Any(x => !Permissions.All.Contains(x) || !actor.Permissions.Contains(x))) throw new DomainException("ROLE_INVALID");
        db.Add(new RoleDefinition(actor.OrganizationId, input.Name, input.Permissions.Distinct().ToArray())); Audit("PERMISSION_CHANGED"); await db.SaveChangesAsync(ct);
    }
    public async Task SetDeviceGroups(Guid id, Guid[]? groups, CancellationToken ct)
    {
        actor.Require("users.manage");
        if (groups?.Length > 100) throw new DomainException("INPUT_INVALID");
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
        if (user.Role == "Owner") throw new DomainException("OWNER_SCOPE_REQUIRED");
        if (groups is not null && await db.Set<DeviceGroup>().CountAsync(x => groups.Contains(x.Id), ct) != groups.Distinct().Count()) throw new DomainException("NOT_FOUND");
        user.DeviceGroupScope = groups is null ? null : System.Text.Json.JsonSerializer.Serialize(groups.Distinct().ToArray());
        Audit("PERMISSION_CHANGED"); await db.SaveChangesAsync(ct);
    }
    private async Task ValidateRole(string role, CancellationToken ct)
    {
        if (role == "Owner" && actor.Role != "Owner") throw new DomainException("FORBIDDEN");
        var permissions = await ResolvePermissions(db, role, ct);
        if (permissions.Count == 0 || permissions.Any(x => !actor.Permissions.Contains(x))) throw new DomainException("FORBIDDEN");
    }
    private void Audit(string type) => db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, null, type, actor.Ip, actor.UserAgent));
}
