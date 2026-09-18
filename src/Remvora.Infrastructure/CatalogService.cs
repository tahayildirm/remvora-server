using Microsoft.EntityFrameworkCore;
using Remvora.Application;
using Remvora.Domain;
namespace Remvora.Infrastructure;

public sealed record CatalogInput(string? Name, Guid? ParentId, string? FirstName, string? LastName, string? DisplayName, string? Phone, string? Email, string? Description, string? CountryCode, string? CountryName, string? Province, string? District, string? City, string? PostalCode, string? AddressLine, double? Latitude, double? Longitude);
public sealed record AssignmentInput(Guid? LocationId, Guid? PrimaryContactId, Guid[] Tags, Guid[] Groups, Guid[] Contacts);

/// <summary>Catalog changes and relationship checks always run inside the current organization's query scope.</summary>
public sealed class CatalogService(Database db, Actor actor)
{
    private static string Permission(string kind) => kind switch { "groups" or "tags" => "groups.manage", "contacts" => "contacts.manage", "locations" => "locations.manage", _ => throw new DomainException("NOT_FOUND") };
    public async Task<object> List(string kind, CancellationToken ct, int offset = 0, int limit = 100)
    {
        actor.Require("devices.view");
        if (actor.DeviceGroups is not null) throw new DomainException("FORBIDDEN");
        var page = new PageRequest(offset, limit);
        return kind switch
        {
            "groups" => await db.Set<DeviceGroup>().AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).ToListAsync(ct),
            "tags" => await db.Set<Tag>().AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).ToListAsync(ct),
            "contacts" => await db.Set<Contact>().AsNoTracking().OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).ToListAsync(ct),
            "locations" => await db.Set<Location>().AsNoTracking().OrderBy(x => x.City).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).ToListAsync(ct),
            _ => throw new DomainException("NOT_FOUND")
        };
    }
    public async Task<object> Save(string kind, Guid? id, CatalogInput input, CancellationToken ct)
    {
        actor.Require(Permission(kind));
        foreach (var value in new[] { input.Name, input.FirstName, input.LastName, input.DisplayName, input.Phone, input.Email, input.Description, input.CountryCode, input.CountryName, input.Province, input.District, input.City, input.PostalCode, input.AddressLine })
            if (value?.Length > 2000) throw new DomainException("INPUT_INVALID");
        TenantEntity entity;
        switch (kind)
        {
            case "groups":
                if (string.IsNullOrWhiteSpace(input.Name)) throw new DomainException("INPUT_INVALID");
                var group = id is null ? new DeviceGroup(actor.OrganizationId, input.Name) : await db.Set<DeviceGroup>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
                var parent = input.ParentId; var visited = new HashSet<Guid> { group.Id };
                while (parent is not null)
                {
                    if (!visited.Add(parent.Value)) throw new DomainException("GROUP_CYCLE");
                    parent = (await db.Set<DeviceGroup>().SingleOrDefaultAsync(x => x.Id == parent, ct) ?? throw new DomainException("NOT_FOUND")).ParentId;
                }
                group.Name = input.Name; group.ParentId = input.ParentId; entity = group; break;
            case "tags":
                if (string.IsNullOrWhiteSpace(input.Name)) throw new DomainException("INPUT_INVALID");
                var tag = id is null ? new Tag(actor.OrganizationId, input.Name) : await db.Set<Tag>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
                tag.Name = input.Name; entity = tag; break;
            case "contacts":
                var contact = id is null ? new Contact(actor.OrganizationId) : await db.Set<Contact>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
                contact.FirstName = input.FirstName ?? ""; contact.LastName = input.LastName ?? ""; contact.DisplayName = input.DisplayName ?? $"{contact.FirstName} {contact.LastName}".Trim();
                if (contact.DisplayName.Length == 0) throw new DomainException("INPUT_INVALID");
                contact.Phone = input.Phone; contact.Email = input.Email; contact.Description = input.Description; entity = contact; break;
            case "locations":
                if (input.Latitude is < -90 or > 90 || input.Longitude is < -180 or > 180) throw new DomainException("INPUT_INVALID");
                var location = id is null ? new Location(actor.OrganizationId) : await db.Set<Location>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("NOT_FOUND");
                location.CountryCode = input.CountryCode; location.CountryName = input.CountryName; location.Province = input.Province; location.District = input.District; location.City = input.City; location.PostalCode = input.PostalCode; location.AddressLine = input.AddressLine; location.Latitude = input.Latitude; location.Longitude = input.Longitude; entity = location; break;
            default: throw new DomainException("NOT_FOUND");
        }
        if (id is null) db.Add(entity);
        db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, null, "CATALOG_UPDATED", actor.Ip, actor.UserAgent));
        await db.SaveChangesAsync(ct); return entity;
    }
    public async Task Delete(string kind, Guid id, CancellationToken ct)
    {
        actor.Require(Permission(kind));
        TenantEntity? entity = kind switch
        {
            "groups" => await db.Set<DeviceGroup>().SingleOrDefaultAsync(x => x.Id == id, ct),
            "tags" => await db.Set<Tag>().SingleOrDefaultAsync(x => x.Id == id, ct),
            "contacts" => await db.Set<Contact>().SingleOrDefaultAsync(x => x.Id == id, ct),
            "locations" => await db.Set<Location>().SingleOrDefaultAsync(x => x.Id == id, ct),
            _ => null
        };
        if (entity is null) throw new DomainException("NOT_FOUND"); db.Remove(entity);
        db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, null, "CATALOG_DELETED", actor.Ip, actor.UserAgent)); await db.SaveChangesAsync(ct);
    }
    public async Task Assign(Guid deviceId, AssignmentInput input, CancellationToken ct)
    {
        if (actor.DeviceGroups is not null) throw new DomainException("FORBIDDEN");
        actor.Require("devices.edit");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new DomainException("NOT_FOUND");
        if (input.Tags.Length + input.Groups.Length + input.Contacts.Length > 1000) throw new DomainException("INPUT_INVALID");
        if (input.LocationId is not null && !await db.Set<Location>().AnyAsync(x => x.Id == input.LocationId, ct)) throw new DomainException("NOT_FOUND");
        var contacts = input.Contacts.Concat(input.PrimaryContactId is Guid primary ? [primary] : Array.Empty<Guid>()).Distinct().ToArray();
        if (await db.Set<Contact>().CountAsync(x => contacts.Contains(x.Id), ct) != contacts.Length ||
            await db.Set<Tag>().CountAsync(x => input.Tags.Contains(x.Id), ct) != input.Tags.Distinct().Count() ||
            await db.Set<DeviceGroup>().CountAsync(x => input.Groups.Contains(x.Id), ct) != input.Groups.Distinct().Count()) throw new DomainException("NOT_FOUND");
        device.Assign(input.LocationId, input.PrimaryContactId);
        db.RemoveRange(await db.Set<DeviceLink>().Where(x => x.DeviceId == deviceId).ToListAsync(ct));
        db.AddRange(input.Tags.Distinct().Select(x => new DeviceLink(actor.OrganizationId, deviceId, tag: x)));
        db.AddRange(input.Groups.Distinct().Select(x => new DeviceLink(actor.OrganizationId, deviceId, group: x)));
        db.AddRange(contacts.Select(x => new DeviceLink(actor.OrganizationId, deviceId, contact: x)));
        db.Audit.Add(new AuditEvent(actor.OrganizationId, actor.UserId, deviceId, "DEVICE_ASSIGNMENTS_UPDATED", actor.Ip, actor.UserAgent)); await db.SaveChangesAsync(ct);
    }
}
