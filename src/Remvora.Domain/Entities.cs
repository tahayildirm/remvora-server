namespace Remvora.Domain;

/// <summary>Base for all tenant-owned records. OrganizationId is immutable outside persistence.</summary>
public abstract class TenantEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public Guid OrganizationId { get; protected set; }
    protected TenantEntity() { }
    protected TenantEntity(Guid organizationId)
    {
        if (organizationId == Guid.Empty) throw new DomainException("ORGANIZATION_REQUIRED");
        OrganizationId = organizationId;
    }
}

public sealed class DomainException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public enum EnrollmentStatus { Created, PendingActivation, Active, Disabled, Revoked }
public enum SessionKind { Terminal, Desktop }

/// <summary>Device lifecycle; external CRUD cannot install identity or activate a device.</summary>
public sealed class Device : TenantEntity
{
    private Device() { }
    public Device(Guid organizationId, string name) : base(organizationId) { Rename(name, null); }
    public string DeviceCode { get; private set; } = Guid.NewGuid().ToString("N");
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public string? PublicKey { get; private set; }
    public string? OperatingSystem { get; private set; }
    public string? Architecture { get; private set; }
    public string? AgentVersion { get; private set; }
    public bool AllowTerminalPrivilegeEscalation { get; private set; }
    public void SetTerminalPrivilegeEscalation(bool allowed)
    { AllowTerminalPrivilegeEscalation = allowed; UpdatedAt = DateTimeOffset.UtcNow; }
    public EnrollmentStatus EnrollmentStatus { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; private set; }
    public Guid? LocationId { get; private set; }
    public Guid? PrimaryContactId { get; private set; }
    public void Rename(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || description?.Length > 2000)
            throw new DomainException("DEVICE_INVALID");
        Name = name.Trim(); Description = description; UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void RequestEnrollment(string publicKey, string os, string architecture, string version)
    {
        if (EnrollmentStatus != EnrollmentStatus.Created) throw new DomainException("ENROLLMENT_STATE_INVALID");
        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 2048 || os.Length > 64 || architecture.Length > 64 || version.Length > 32)
            throw new DomainException("IDENTITY_INVALID");
        PublicKey = publicKey; OperatingSystem = os; Architecture = architecture; AgentVersion = version;
        EnrollmentStatus = EnrollmentStatus.PendingActivation; UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void Activate()
    {
        if (EnrollmentStatus != EnrollmentStatus.PendingActivation || PublicKey is null) throw new DomainException("ENROLLMENT_STATE_INVALID");
        EnrollmentStatus = EnrollmentStatus.Active; ActivatedAt = UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void Disable()
    {
        if (EnrollmentStatus != EnrollmentStatus.Active) throw new DomainException("DEVICE_NOT_ACTIVE");
        EnrollmentStatus = EnrollmentStatus.Disabled; UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void Enable()
    {
        if (EnrollmentStatus != EnrollmentStatus.Disabled || PublicKey is null) throw new DomainException("ENROLLMENT_STATE_INVALID");
        EnrollmentStatus = EnrollmentStatus.Active; UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void Revoke() { EnrollmentStatus = EnrollmentStatus.Revoked; UpdatedAt = DateTimeOffset.UtcNow; }
    public void Heartbeat()
    {
        if (EnrollmentStatus != EnrollmentStatus.Active) throw new DomainException("DEVICE_NOT_ACTIVE");
        LastSeenAt = DateTimeOffset.UtcNow;
    }
    public void Assign(Guid? locationId, Guid? contactId) { LocationId = locationId; PrimaryContactId = contactId; }
}

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
}
public sealed class User : TenantEntity
{
    private User() { }
    public User(Guid org, string email, string passwordHash) : base(org) { Email = email; PasswordHash = passwordHash; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Viewer";
    public string? DeviceGroupScope { get; set; }
    public string? ProtectedTotpSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public long LastTotpStep { get; set; } = -1;
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
public sealed class UserSession : TenantEntity
{
    private UserSession() { }
    public UserSession(Guid org, Guid user, string hash, DateTimeOffset expires) : base(org) { UserId = user; TokenHash = hash; ExpiresAt = expires; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class RecoveryCode : TenantEntity
{
    private RecoveryCode() { }
    public RecoveryCode(Guid org, Guid user, string hash) : base(org) { UserId = user; Hash = hash; }
    public Guid UserId { get; set; }
    public string Hash { get; set; } = "";
    public bool Used { get; set; }
}
public sealed class ApiKey : TenantEntity
{
    private ApiKey() { }
    public ApiKey(Guid org, string name, string hash, string[] scopes, DateTimeOffset expires) : base(org)
    { Name = name; TokenHash = hash; Scopes = scopes; ExpiresAt = expires; }
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
public sealed class DeviceGroup : TenantEntity
{
    private DeviceGroup() { }
    public DeviceGroup(Guid org, string name, Guid? parent = null) : base(org) { Name = name; ParentId = parent; }
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
}
public sealed class Tag : TenantEntity
{
    private Tag() { }
    public Tag(Guid org, string name) : base(org) { Name = name; }
    public string Name { get; set; } = "";
}
public sealed class Contact : TenantEntity
{
    private Contact() { }
    public Contact(Guid org) : base(org) { }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Description { get; set; }
}
public sealed class Location : TenantEntity
{
    private Location() { }
    public Location(Guid org) : base(org) { }
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public string? Province { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? AddressLine { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}
public sealed class DeviceLink : TenantEntity
{
    private DeviceLink() { }
    public DeviceLink(Guid org, Guid device, Guid? tag = null, Guid? group = null, Guid? contact = null) : base(org)
    { DeviceId = device; TagId = tag; GroupId = group; ContactId = contact; }
    public Guid DeviceId { get; set; }
    public Guid? TagId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? ContactId { get; set; }
}
public sealed class Enrollment : TenantEntity
{
    private Enrollment() { }
    public Enrollment(Guid org, Guid device, string hash, DateTimeOffset expires) : base(org) { DeviceId = device; TokenHash = hash; ExpiresAt = expires; }
    public Guid DeviceId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public bool Approved { get; set; }
}
public sealed class Challenge : TenantEntity
{
    private Challenge() { }
    public Challenge(Guid org, Guid device, string value, string purpose, DateTimeOffset expires) : base(org)
    { DeviceId = device; Value = value; Purpose = purpose; ExpiresAt = expires; }
    public Guid DeviceId { get; set; }
    public string Value { get; set; } = "";
    public string Purpose { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
public sealed class RemoteSession : TenantEntity
{
    private RemoteSession() { }
    public RemoteSession(Guid org, Guid user, Guid device, SessionKind kind, string hash) : base(org)
    { UserId = user; DeviceId = device; Kind = kind; TokenHash = hash; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public SessionKind Kind { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(1);
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
public sealed class AuditEvent : TenantEntity
{
    private AuditEvent() { }
    public AuditEvent(Guid org, Guid? user, Guid? device, string type, string? ip, string? userAgent) : base(org)
    { UserId = user; DeviceId = device; EventType = type; Ip = ip; UserAgent = userAgent; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string EventType { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string Metadata { get; set; } = "{}";
}

/// <summary>Organization-defined permission sets; reserved built-in roles cannot be overwritten.</summary>
public sealed class RoleDefinition : TenantEntity
{
    private RoleDefinition() { }
    public RoleDefinition(Guid org, string name, string[] permissions) : base(org) { Name = name; Permissions = permissions; }
    public string Name { get; set; } = "";
    public string[] Permissions { get; set; } = [];
}
