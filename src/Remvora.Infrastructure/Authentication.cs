using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Remvora.Application;
using Remvora.Domain;
namespace Remvora.Infrastructure;

public static class Tokens
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>RFC 6238 TOTP, six digits and 30-second steps. Caller persists the accepted step to prevent replay.</summary>
public static class Totp
{
    public static string Generate(byte[] secret, long step)
    {
        Span<byte> bytes = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, step);
        var hash = HMACSHA1.HashData(secret, bytes); var offset = hash[^1] & 15;
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
    public static long Verify(byte[] secret, string code, long previous)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return -1;
        var current = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var step = current - 1; step <= current + 1; step++)
            if (step > previous && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Generate(secret, step)), Encoding.ASCII.GetBytes(code))) return step;
        return -1;
    }
    public static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var output = new StringBuilder(); var buffer = 0; var bits = 0;
        foreach (var value in bytes) { buffer = (buffer << 8) | value; bits += 8; while (bits >= 5) { bits -= 5; output.Append(alphabet[(buffer >> bits) & 31]); } }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]); return output.ToString();
    }
}

/// <summary>Opaque server-side sessions: database stores only hashes, rotation atomically revokes the predecessor.</summary>
public sealed class AuthenticationService(Database db, Actor actor, IPasswordHasher<User> hasher, IDataProtectionProvider protection, Microsoft.Extensions.Options.IOptions<SecurityPolicy> policy)
{
    private readonly IDataProtector protector = protection.CreateProtector("Remvora.Totp.v1");
    private static readonly User Dummy = new(Guid.NewGuid(), "invalid", "");
    private readonly string dummyHash = hasher.HashPassword(Dummy, Tokens.New());
    public async Task<string> Login(LoginInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrEmpty(input.Password) || input.Email.Length > 254 || input.Password.Length > 256) throw new DomainException("LOGIN_FAILED");
        if (input.OrganizationId is null)
        {
            // Discovery only selects a candidate; normal tenant-scoped password, lockout and MFA checks below still apply.
            var normalized = input.Email.Trim().ToUpperInvariant();
            var candidates = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(x => x.Email == normalized).OrderBy(x => x.Id).Take(100).ToListAsync(ct);
            var candidate = candidates.FirstOrDefault(x => x.LockedUntil <= DateTimeOffset.UtcNow || x.LockedUntil == null);
            candidate = candidates.FirstOrDefault(x => (x.LockedUntil == null || x.LockedUntil <= DateTimeOffset.UtcNow) && hasher.VerifyHashedPassword(x, x.PasswordHash, input.Password) != PasswordVerificationResult.Failed) ?? candidate;
            if (candidate is null) { hasher.VerifyHashedPassword(Dummy, dummyHash, input.Password); throw new DomainException("LOGIN_FAILED"); }
            input = input with { OrganizationId = candidate.OrganizationId };
        }
        actor.OrganizationId = input.OrganizationId.Value;
        var email = input.Email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email, ct);
        var valid = hasher.VerifyHashedPassword(user ?? Dummy, user?.PasswordHash ?? dummyHash, input.Password) != PasswordVerificationResult.Failed;
        if (user is null) throw new DomainException("LOGIN_FAILED");
        if (user.LockedUntil > DateTimeOffset.UtcNow) throw new DomainException("LOGIN_FAILED");
        if (valid && user.TwoFactorEnabled) valid = await VerifySecondFactor(user, input.Code ?? "", ct);
        if (!valid)
        {
            user.FailedAttempts++; if (user.FailedAttempts >= 5) user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            Audit("USER_LOGIN_FAILED", user.Id); await db.SaveChangesAsync(ct); throw new DomainException("LOGIN_FAILED");
        }
        user.FailedAttempts = 0; user.LockedUntil = null;
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, input.Password);
        var token = Tokens.New(); db.Sessions.Add(new UserSession(actor.OrganizationId, user.Id, Tokens.Hash(token), DateTimeOffset.UtcNow.AddMinutes(policy.Value.UserSessionMinutes)));
        Audit("USER_LOGIN", user.Id); await db.SaveChangesAsync(ct); return token;
    }
    public async Task<object> Profile(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == actor.UserId, ct);
        var organization = await db.Set<Organization>().AsNoTracking().SingleAsync(x => x.Id == actor.OrganizationId, ct);
        return new { user.Email, user.Role, user.TwoFactorEnabled, organizationId = organization.Id, organizationName = organization.Name };
    }
    private async Task<User> VerifyCurrentPassword(string password, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(x => x.Id == actor.UserId, ct);
        if (user.LockedUntil > DateTimeOffset.UtcNow) throw new DomainException("LOGIN_FAILED");
        if (string.IsNullOrEmpty(password) || password.Length > 256 || hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            user.FailedAttempts++; if (user.FailedAttempts >= 5) user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            Audit("USER_REAUTH_FAILED", user.Id); await db.SaveChangesAsync(ct); throw new DomainException("LOGIN_FAILED");
        }
        user.FailedAttempts = 0; user.LockedUntil = null; await db.SaveChangesAsync(ct); return user;
    }
    public async Task<object> Organizations(string password, CancellationToken ct)
    {
        var current = await VerifyCurrentPassword(password, ct);
        // Never grant membership by email alone. Target login also requires its own MFA.
        var accounts = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(x => x.Email == current.Email && (x.LockedUntil == null || x.LockedUntil <= DateTimeOffset.UtcNow)).Take(100).ToListAsync(ct);
        var ids = accounts.Where(x => hasher.VerifyHashedPassword(x, x.PasswordHash, password) != PasswordVerificationResult.Failed).Select(x => x.OrganizationId).ToArray();
        return await db.Set<Organization>().AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
    }
    public async Task<string> UpdateProfile(ProfileInput input, CancellationToken ct)
    {
        var email = input.Email.Trim().ToUpperInvariant();
        if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email || (input.NewPassword is not null && input.NewPassword.Length is < 8 or > 256)) throw new DomainException("INPUT_INVALID");
        var user = await VerifyCurrentPassword(input.CurrentPassword, ct);
        if (await db.Users.AnyAsync(x => x.Id != user.Id && x.Email == email, ct)) throw new DomainException("DATA_CONFLICT");
        user.Email = email;
        if (input.NewPassword is not null) user.PasswordHash = hasher.HashPassword(user, input.NewPassword);
        var sessions = await db.Sessions.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions) session.RevokedAt = DateTimeOffset.UtcNow;
        var token = Tokens.New(); db.Sessions.Add(new UserSession(actor.OrganizationId, user.Id, Tokens.Hash(token), DateTimeOffset.UtcNow.AddMinutes(policy.Value.UserSessionMinutes)));
        Audit("USER_PROFILE_UPDATED", user.Id); await db.SaveChangesAsync(ct); return token;
    }
    private async Task<bool> VerifySecondFactor(User user, string code, CancellationToken ct)
    {
        if (user.ProtectedTotpSecret is null) return false;
        var step = Totp.Verify(Convert.FromBase64String(protector.Unprotect(user.ProtectedTotpSecret)), code, user.LastTotpStep);
        if (step >= 0) { user.LastTotpStep = step; return true; }
        var hash = Tokens.Hash(code);
        var recovery = await db.Set<RecoveryCode>().SingleOrDefaultAsync(x => x.UserId == user.Id && x.Hash == hash && !x.Used, ct);
        if (recovery is null) return false; recovery.Used = true; return true;
    }
    public async Task<bool> Resolve(string? sessionToken, string? apiToken, CancellationToken ct)
    {
        // These are the only tenant-independent credential lookups; random 256-bit credentials identify a tenant.
        if (apiToken is not null)
        {
            var hash = Tokens.Hash(apiToken);
            var key = await db.ApiKeys.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct);
            if (key is null) return false;
            actor.OrganizationId = key.OrganizationId; actor.IsApiKey = true;
            actor.Permissions = key.Scopes.Select(Permissions.ForScope).OfType<string>().ToHashSet();
            key.LastUsedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return true;
        }
        if (sessionToken is null) return false;
        var sessionHash = Tokens.Hash(sessionToken);
        var session = await db.Sessions.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == sessionHash && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (session is null) return false;
        actor.OrganizationId = session.OrganizationId;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.UserId, ct);
        if (user is null) return false;
        actor.DeviceGroups = user.DeviceGroupScope is null ? null : System.Text.Json.JsonSerializer.Deserialize<Guid[]>(user.DeviceGroupScope) ?? [];
        actor.UserId = user.Id; actor.SessionId = session.Id; actor.Role = user.Role; actor.Permissions = await UserAdministration.ResolvePermissions(db, user.Role, ct); return true;
    }
    public async Task<string> Rotate(CancellationToken ct)
    {
        var old = await db.Sessions.SingleOrDefaultAsync(x => x.Id == actor.SessionId && x.RevokedAt == null, ct) ?? throw new DomainException("UNAUTHORIZED");
        old.RevokedAt = DateTimeOffset.UtcNow;
        var token = Tokens.New(); db.Sessions.Add(new UserSession(actor.OrganizationId, old.UserId, Tokens.Hash(token), DateTimeOffset.UtcNow.AddMinutes(policy.Value.UserSessionMinutes)));
        await db.SaveChangesAsync(ct); return token;
    }
    public async Task Logout(bool all, CancellationToken ct)
    {
        var sessions = await db.Sessions.Where(x => x.UserId == actor.UserId && (all || x.Id == actor.SessionId) && x.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions) session.RevokedAt = DateTimeOffset.UtcNow;
        Audit("USER_LOGOUT", actor.UserId); await db.SaveChangesAsync(ct);
    }
    public async Task<object> Sessions(CancellationToken ct) => await db.Sessions.AsNoTracking()
        .Where(x => x.UserId == actor.UserId && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow)
        .OrderByDescending(x => x.ExpiresAt).Select(x => new { x.Id, x.ExpiresAt, current = x.Id == actor.SessionId }).ToListAsync(ct);
    public async Task RevokeSession(Guid id, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == id && x.UserId == actor.UserId, ct) ?? throw new DomainException("NOT_FOUND");
        session.RevokedAt = DateTimeOffset.UtcNow;
        Audit("USER_SESSION_REVOKED", actor.UserId); await db.SaveChangesAsync(ct);
    }
    public async Task<object> BeginTotp(CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(x => x.Id == actor.UserId, ct);
        if (user.TwoFactorEnabled) throw new DomainException("TWO_FACTOR_ALREADY_ENABLED");
        var secret = RandomNumberGenerator.GetBytes(20); var encoded = Totp.Base32(secret);
        user.ProtectedTotpSecret = protector.Protect(Convert.ToBase64String(secret)); await db.SaveChangesAsync(ct);
        return new { secret = encoded, uri = $"otpauth://totp/Remvora:{Uri.EscapeDataString(user.Email)}?secret={encoded}&issuer=Remvora&digits=6&period=30" };
    }
    public async Task<string[]> ConfirmTotp(string code, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(x => x.Id == actor.UserId, ct);
        if (user.TwoFactorEnabled || user.ProtectedTotpSecret is null) throw new DomainException("TWO_FACTOR_STATE_INVALID");
        var step = Totp.Verify(Convert.FromBase64String(protector.Unprotect(user.ProtectedTotpSecret)), code, user.LastTotpStep);
        if (step < 0) throw new DomainException("CODE_INVALID");
        user.LastTotpStep = step; user.TwoFactorEnabled = true;
        var codes = Enumerable.Range(0, 10).Select(_ => Tokens.New()[..24]).ToArray();
        db.Set<RecoveryCode>().AddRange(codes.Select(x => new RecoveryCode(actor.OrganizationId, user.Id, Tokens.Hash(x))));
        Audit("USER_2FA_ENABLED", user.Id); await db.SaveChangesAsync(ct); return codes;
    }
    public async Task<string> CreateApiKey(ApiKeyInput input, CancellationToken ct)
    {
        actor.Require("apiKeys.manage");
        if (input.ExpiresAt <= DateTimeOffset.UtcNow || input.ExpiresAt > DateTimeOffset.UtcNow.AddYears(1) || input.Scopes.Length == 0 || input.Scopes.Any(x => Permissions.ForScope(x) is null) || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128)
            throw new DomainException("API_KEY_INVALID");
        var token = Tokens.New(); db.ApiKeys.Add(new ApiKey(actor.OrganizationId, input.Name, Tokens.Hash(token), input.Scopes.Distinct().ToArray(), input.ExpiresAt));
        Audit("API_KEY_CREATED", actor.UserId); await db.SaveChangesAsync(ct); return token;
    }
    public async Task RevokeApiKey(Guid id, CancellationToken ct)
    {
        actor.Require("apiKeys.manage"); var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
        key.RevokedAt = DateTimeOffset.UtcNow; Audit("API_KEY_REVOKED", actor.UserId); await db.SaveChangesAsync(ct);
    }
    private void Audit(string type, Guid? user) => db.Audit.Add(new AuditEvent(actor.OrganizationId, user, null, type, actor.Ip, actor.UserAgent));
}
