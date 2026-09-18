using Microsoft.EntityFrameworkCore;
using Remvora.Application;
using Remvora.Domain;
namespace Remvora.Infrastructure;

/// <summary>Tenant-scoped reads and fail-closed writes; authentication uses explicitly bounded lookups.</summary>
public class Database(DbContextOptions options, Actor actor) : DbContext(options)
{
    public Guid TenantId => actor.OrganizationId;
    public bool GroupRestricted => actor.DeviceGroups is not null;
    public Guid[] AllowedGroups => actor.DeviceGroups ?? [];
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Organization>().HasKey(x => x.Id);
        model.Entity<ApiKey>().Property(x => x.Scopes).HasConversion(
            values => System.Text.Json.JsonSerializer.Serialize(values, (System.Text.Json.JsonSerializerOptions?)null),
            json => System.Text.Json.JsonSerializer.Deserialize<string[]>(json, (System.Text.Json.JsonSerializerOptions?)null)!)
            .Metadata.SetValueComparer(new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<string[]>(
                (a, b) => a!.SequenceEqual(b!), values => values.Aggregate(0, (hash, value) => HashCode.Combine(hash, value.GetHashCode())), values => values.ToArray()));
        model.Entity<RoleDefinition>().Property(x => x.Permissions).HasConversion(
            values => System.Text.Json.JsonSerializer.Serialize(values, (System.Text.Json.JsonSerializerOptions?)null),
            json => System.Text.Json.JsonSerializer.Deserialize<string[]>(json, (System.Text.Json.JsonSerializerOptions?)null)!)
            .Metadata.SetValueComparer(new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<string[]>(
                (a, b) => a!.SequenceEqual(b!), values => values.Aggregate(0, (hash, value) => HashCode.Combine(hash, value.GetHashCode())), values => values.ToArray()));
        model.Entity<RoleDefinition>().Property(x => x.Name).HasMaxLength(64);
        model.Entity<RoleDefinition>().HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        model.Entity<User>().Property(x => x.Email).HasMaxLength(254);
        model.Entity<ApiKey>().Property(x => x.TokenHash).HasMaxLength(64);
        model.Entity<UserSession>().Property(x => x.TokenHash).HasMaxLength(64);
        model.Entity<Enrollment>().Property(x => x.TokenHash).HasMaxLength(64);
        model.Entity<Device>().Property(x => x.DeviceCode).HasMaxLength(32);
        model.Entity<Tag>().Property(x => x.Name).HasMaxLength(128);
        Configure<Device>(model); Configure<User>(model); Configure<UserSession>(model); Configure<ApiKey>(model);
        Configure<DeviceGroup>(model); Configure<Tag>(model); Configure<Contact>(model); Configure<Location>(model);
        Configure<DeviceLink>(model); Configure<Enrollment>(model); Configure<Challenge>(model); Configure<RemoteSession>(model);
        Configure<AuditEvent>(model); Configure<RecoveryCode>(model); Configure<RoleDefinition>(model);
        model.Entity<User>().Property(x => x.DeviceGroupScope).HasMaxLength(4096);
        model.Entity<Device>().HasQueryFilter(x => x.OrganizationId == TenantId && (!GroupRestricted || Set<DeviceLink>().Any(link => link.OrganizationId == TenantId && link.DeviceId == x.Id && link.GroupId != null && AllowedGroups.Contains(link.GroupId.Value))));
        model.Entity<Device>().HasIndex(x => x.DeviceCode).IsUnique();
        model.Entity<User>().HasIndex(x => new { x.OrganizationId, x.Email }).IsUnique();
        model.Entity<ApiKey>().HasIndex(x => x.TokenHash).IsUnique();
        model.Entity<UserSession>().HasIndex(x => x.TokenHash).IsUnique();
        model.Entity<Enrollment>().HasIndex(x => x.TokenHash).IsUnique();
        model.Entity<Tag>().HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        model.Entity<Device>().HasOne<Location>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LocationId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Device>().HasOne<Contact>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PrimaryContactId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeviceGroup>().HasOne<DeviceGroup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ParentId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeviceLink>().HasOne<Device>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DeviceId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id });
        model.Entity<DeviceLink>().HasOne<Tag>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TagId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeviceLink>().HasOne<DeviceGroup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.GroupId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeviceLink>().HasOne<Contact>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ContactId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Enrollment>().Property(x => x.ConsumedAt).IsConcurrencyToken();
        model.Entity<Challenge>().Property(x => x.ConsumedAt).IsConcurrencyToken();
        model.Entity<RemoteSession>().Property(x => x.ConsumedAt).IsConcurrencyToken();
        model.Entity<UserSession>().Property(x => x.RevokedAt).IsConcurrencyToken();
        model.Entity<User>().Property(x => x.LastTotpStep).IsConcurrencyToken();
        model.Entity<User>().Property(x => x.FailedAttempts).IsConcurrencyToken();
        model.Entity<RecoveryCode>().Property(x => x.Used).IsConcurrencyToken();
        model.Entity<Device>().Property(x => x.EnrollmentStatus).IsConcurrencyToken();
    }
    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<DateTimeOffset>().HaveConversion<UtcTimestampConverter>();
    }
    private void Configure<T>(ModelBuilder model) where T : TenantEntity
    {
        var entity = model.Entity<T>(); entity.HasKey(x => x.Id);
        entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
        entity.HasQueryFilter(x => x.OrganizationId == TenantId);
        entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
    public override Task<int> SaveChangesAsync(CancellationToken ct = default) => SaveChangesAsync(true, ct);
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<TenantEntity>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (TenantId == Guid.Empty || entry.Entity.OrganizationId != TenantId ||
                (entry.State != EntityState.Added && entry.Property(x => x.OrganizationId).IsModified))
                throw new DomainException("TENANT_BOUNDARY_VIOLATION");
            if (entry.Entity is AuditEvent && entry.State != EntityState.Added) throw new DomainException("AUDIT_IMMUTABLE");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw new InvalidOperationException("Use guarded asynchronous persistence.");
    public override int SaveChanges() => throw new InvalidOperationException("Use guarded asynchronous persistence.");
}
public sealed class DeviceRepository(Database db, Actor actor) : IDeviceRepository
{
    public async Task<IReadOnlyList<Device>> List(CancellationToken ct, int offset = 0, int limit = 100) => await db.Devices.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(ct);
    public Task<Device?> Find(Guid id, CancellationToken ct) => db.Devices.SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task Delete(Device device, CancellationToken ct)
    {
        db.Set<DeviceLink>().RemoveRange(await db.Set<DeviceLink>().Where(x => x.DeviceId == device.Id).ToListAsync(ct));
        db.Enrollments.RemoveRange(await db.Enrollments.Where(x => x.DeviceId == device.Id).ToListAsync(ct));
        db.Challenges.RemoveRange(await db.Challenges.Where(x => x.DeviceId == device.Id).ToListAsync(ct));
        db.RemoteSessions.RemoveRange(await db.RemoteSessions.Where(x => x.DeviceId == device.Id).ToListAsync(ct));
        db.Devices.Remove(device);
        db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, device.Id, "DEVICE_DELETED_PERMANENTLY", actor.Ip, actor.UserAgent));
        // One SaveChanges transaction keeps deletion and its audit record atomic.
        await db.SaveChangesAsync(ct);
    }
    public async Task Save(Device device, bool isNew, string eventType, CancellationToken ct)
    {
        if (isNew) db.Devices.Add(device);
        db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, device.Id, eventType, actor.Ip, actor.UserAgent));
        await db.SaveChangesAsync(ct);
    }
}

public sealed class UtcTimestampConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTime>(
    value => value.UtcDateTime, value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
public sealed class PostgresDatabase(DbContextOptions<PostgresDatabase> options, Actor actor) : Database(options, actor);
public sealed class MySqlDatabase(DbContextOptions<MySqlDatabase> options, Actor actor) : Database(options, actor);
public sealed class MariaDbDatabase(DbContextOptions<MariaDbDatabase> options, Actor actor) : Database(options, actor);
