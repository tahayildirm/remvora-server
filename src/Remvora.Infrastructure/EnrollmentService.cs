using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Remvora.Application;
using Remvora.Domain;
namespace Remvora.Infrastructure;

/// <summary>One-time enrollment and P-256 device authentication. Signatures bind challenge, purpose and device.</summary>
public sealed class EnrollmentService(Database db, Actor actor, Microsoft.Extensions.Options.IOptions<SecurityPolicy> policy)
{
    public async Task<object> Issue(Guid deviceId, CancellationToken ct)
    {
        actor.Require("enrollment.manage");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new DomainException("NOT_FOUND");
        if (device.EnrollmentStatus != EnrollmentStatus.Created) throw new DomainException("ENROLLMENT_STATE_INVALID");
        var token = Tokens.New(); var expires = DateTimeOffset.UtcNow.AddMinutes(policy.Value.EnrollmentMinutes);
        db.Enrollments.Add(new Enrollment(actor.OrganizationId, deviceId, Tokens.Hash(token), expires)); await db.SaveChangesAsync(ct);
        return new { token, expiresAt = expires };
    }
    public async Task<object> Request(EnrollmentInput input, CancellationToken ct)
    {
        var hash = Tokens.Hash(input.Token);
        var enrollment = await db.Enrollments.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash && x.ConsumedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct) ?? throw new DomainException("ENROLLMENT_INVALID");
        using var key = ECDsa.Create();
        try
        {
            var bytes = Convert.FromBase64String(input.PublicKey); key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new DomainException("IDENTITY_INVALID");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { throw new DomainException("IDENTITY_INVALID"); }
        actor.OrganizationId = enrollment.OrganizationId;
        var device = await db.Devices.SingleAsync(x => x.Id == enrollment.DeviceId, ct);
        device.RequestEnrollment(input.PublicKey, input.OperatingSystem, input.Architecture, input.AgentVersion);
        enrollment.ConsumedAt = DateTimeOffset.UtcNow;
        db.Audit.Add(new AuditEvent(actor.OrganizationId, null, device.Id, "DEVICE_ENROLLMENT_REQUESTED", actor.Ip, actor.UserAgent));
        await db.SaveChangesAsync(ct); return new { deviceId = device.Id, status = "PendingActivation" };
    }
    public async Task Approve(Guid deviceId, CancellationToken ct)
    {
        actor.Require("enrollment.manage");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new DomainException("NOT_FOUND");
        if (device.EnrollmentStatus != EnrollmentStatus.PendingActivation) throw new DomainException("ENROLLMENT_STATE_INVALID");
        var enrollment = await db.Enrollments.SingleAsync(x => x.DeviceId == deviceId && x.ConsumedAt != null, ct);
        enrollment.Approved = true; db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, deviceId, "DEVICE_ENROLLMENT_APPROVED", actor.Ip, actor.UserAgent));
        await db.SaveChangesAsync(ct);
    }
    public async Task<object> Challenge(Guid deviceId, string purpose, CancellationToken ct)
    {
        if (purpose is not ("activate" or "connect")) throw new DomainException("CHALLENGE_INVALID");
        // Device ID is a locator, never a credential; no organization metadata is returned.
        var device = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new DomainException("CHALLENGE_INVALID");
        actor.OrganizationId = device.OrganizationId;
        if (purpose == "activate")
        {
            if (device.EnrollmentStatus != EnrollmentStatus.PendingActivation || !await db.Enrollments.AnyAsync(x => x.DeviceId == deviceId && x.Approved, ct)) throw new DomainException("CHALLENGE_INVALID");
        }
        else if (device.EnrollmentStatus != EnrollmentStatus.Active) throw new DomainException("CHALLENGE_INVALID");
        var nonce = Tokens.New(); var value = $"remvora:v1:{purpose}:{deviceId:D}:{nonce}";
        var challenge = new Challenge(actor.OrganizationId, deviceId, value, purpose, DateTimeOffset.UtcNow.AddSeconds(policy.Value.ChallengeSeconds));
        db.Challenges.Add(challenge); await db.SaveChangesAsync(ct);
        return new { challengeId = challenge.Id, challenge = value, expiresAt = challenge.ExpiresAt };
    }
    public async Task<Device> Verify(ProofInput input, string purpose, CancellationToken ct)
    {
        var challenge = await db.Challenges.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == input.ChallengeId && x.Purpose == purpose && x.ConsumedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct) ?? throw new DomainException("PROOF_INVALID");
        actor.OrganizationId = challenge.OrganizationId;
        var device = await db.Devices.SingleAsync(x => x.Id == challenge.DeviceId, ct);
        if (device.PublicKey is null || (purpose == "connect" && device.EnrollmentStatus != EnrollmentStatus.Active)) throw new DomainException("PROOF_INVALID");
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(device.PublicKey), out _);
            if (!key.VerifyData(Encoding.UTF8.GetBytes(challenge.Value), Convert.FromBase64String(input.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new DomainException("PROOF_INVALID");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { throw new DomainException("PROOF_INVALID"); }
        challenge.ConsumedAt = DateTimeOffset.UtcNow;
        if (purpose == "activate")
        {
            if (!await db.Enrollments.AnyAsync(x => x.DeviceId == device.Id && x.Approved, ct)) throw new DomainException("PROOF_INVALID");
            device.Activate(); db.Audit.Add(new AuditEvent(actor.OrganizationId, null, device.Id, "DEVICE_ACTIVATED", actor.Ip, actor.UserAgent));
        }
        else device.Heartbeat();
        await db.SaveChangesAsync(ct); return device;
    }
}
