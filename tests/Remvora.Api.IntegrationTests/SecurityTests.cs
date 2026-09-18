using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Remvora.Application;
using Remvora.Domain;
using Remvora.Infrastructure;
namespace Remvora.Api.IntegrationTests;

public sealed class Factory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development").ConfigureServices(services => services.AddScoped<Database>(scope =>
    {
        var connection = Environment.GetEnvironmentVariable("REMVORA_TEST_DATABASE") ?? throw new InvalidOperationException("Test database required");
        var actor = scope.GetRequiredService<Actor>();
        return Environment.GetEnvironmentVariable("REMVORA_TEST_PROVIDER") switch
        {
            "MySQL" => new MySqlDatabase(new DbContextOptionsBuilder<MySqlDatabase>().UseMySql(connection, new MySqlServerVersion(new Version(8, 4, 0))).Options, actor),
            "MariaDB" => new MariaDbDatabase(new DbContextOptionsBuilder<MariaDbDatabase>().UseMySql(connection, new MariaDbServerVersion(new Version(11, 4, 0))).Options, actor),
            _ => new PostgresDatabase(new DbContextOptionsBuilder<PostgresDatabase>().UseNpgsql(connection).Options, actor)
        };
    })).ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Database"] = Environment.GetEnvironmentVariable("REMVORA_TEST_DATABASE") ?? throw new InvalidOperationException("Set REMVORA_TEST_DATABASE to an isolated PostgreSQL database"),
        ["Database:Provider"] = Environment.GetEnvironmentVariable("REMVORA_TEST_PROVIDER") ?? "PostgreSQL",
        ["Database:ServerVersion"] = Environment.GetEnvironmentVariable("REMVORA_TEST_VERSION"),
        ["Security:AllowedOrigin"] = "https://localhost",
        ["AllowedHosts"] = "localhost"
    }));
}
public class SecurityTests
{
    private const string Password = "Test-only-Random-Password-9!";
    private static async Task<(Guid Org, Guid Device)> Seed(Factory factory, string role = "Owner")
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<Database>(); await db.Database.MigrateAsync();
        var org = new Organization { Name = "test" }; scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = org.Id;
        var user = new User(org.Id, "TEST@EXAMPLE.INVALID", "") { Role = role };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
        var device = new Device(org.Id, "test-device"); db.Add(org); db.Add(user); db.Add(device); await db.SaveChangesAsync(); return (org.Id, device.Id);
    }
    private static HttpClient Client(Factory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost"); client.DefaultRequestHeaders.Add("X-Remvora-Request", "1"); return client;
    }
    private static async Task Login(HttpClient client, Guid org, string? code = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(org, "test@example.invalid", Password, code)); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    [Fact]
    public async Task PermanentDeletionRequiresConfirmationAndRemovesOperationalRecordsOnly()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory);
        string code;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org;
            var db = scope.ServiceProvider.GetRequiredService<Database>();
            code = (await db.Devices.SingleAsync(x => x.Id == a.Device)).DeviceCode;
            db.Add(new DeviceLink(a.Org, a.Device));
            db.Add(new Enrollment(a.Org, a.Device, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1)));
            db.Add(new Challenge(a.Org, a.Device, "test", "connect", DateTimeOffset.UtcNow.AddHours(1)));
            db.Add(new RemoteSession(a.Org, (await db.Users.SingleAsync()).Id, a.Device, SessionKind.Desktop, Guid.NewGuid().ToString()));
            db.Add(new AuditEvent(a.Org, null, a.Device, "TEST_HISTORY", null, null));
            await db.SaveChangesAsync();
        }
        using var client = Client(factory); await Login(client, a.Org);
        var endpoint = $"/api/v1/devices/{a.Device}/delete-permanently";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint, new PermanentDeleteInput("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/devices/{b.Device}/delete-permanently", new PermanentDeleteInput(code))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/devices/{a.Device}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/devices/{a.Device}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(endpoint, new PermanentDeleteInput(code))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(endpoint, new PermanentDeleteInput(code))).StatusCode);
        using var verify = factory.Services.CreateScope(); verify.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org;
        var result = verify.ServiceProvider.GetRequiredService<Database>();
        Assert.False(await result.Devices.AnyAsync(x => x.Id == a.Device));
        Assert.False(await result.Set<DeviceLink>().AnyAsync(x => x.DeviceId == a.Device));
        Assert.False(await result.Enrollments.AnyAsync(x => x.DeviceId == a.Device));
        Assert.False(await result.Challenges.AnyAsync(x => x.DeviceId == a.Device));
        Assert.False(await result.RemoteSessions.AnyAsync(x => x.DeviceId == a.Device));
        Assert.True(await result.Audit.AnyAsync(x => x.DeviceId == a.Device && x.EventType == "TEST_HISTORY"));
        Assert.True(await result.Audit.AnyAsync(x => x.DeviceId == a.Device && x.EventType == "DEVICE_DELETED_PERMANENTLY"));
    }
    [Fact]
    public async Task ViewerCannotPermanentlyDeleteDevice()
    {
        using var factory = new Factory(); var a = await Seed(factory, "Viewer");
        using var client = Client(factory); await Login(client, a.Org);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/devices/{a.Device}/delete-permanently", new PermanentDeleteInput("any"))).StatusCode);
    }
    [Fact]
    public async Task OwnerUserManagementIsTenantScopedAndDoesNotExposeSecrets()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory);
        using var owner = Client(factory); await Login(owner, a.Org);
        var created = await owner.PostAsJsonAsync("/api/v1/users", new UserInput("managed@example.invalid", Password, "Viewer"));
        var user = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = user.GetProperty("id").GetGuid();
        using var managed = Client(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await managed.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "managed@example.invalid", Password, null))).StatusCode);
        var users = await owner.GetStringAsync("/api/v1/users");
        Assert.Contains("twoFactorEnabled", users); Assert.Contains("activeSessions", users);
        Assert.DoesNotContain("passwordHash", users); Assert.DoesNotContain("protectedTotpSecret", users);
        Assert.Equal(HttpStatusCode.Forbidden, (await managed.PutAsJsonAsync($"/api/v1/users/{id}", new UserUpdateInput("x@example.invalid", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v1/users/{id}", new UserUpdateInput("managed@example.invalid", "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v1/users/{id}", new UserUpdateInput("changed@example.invalid", "new-password-8", true, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await managed.GetAsync("/api/v1/auth/me")).StatusCode);
        Guid foreignUser;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = b.Org;
            foreignUser = (await scope.ServiceProvider.GetRequiredService<Database>().Users.SingleAsync()).Id;
        }
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/v1/users/{foreignUser}", new UserUpdateInput("x@example.invalid", null))).StatusCode);
        var states = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices/presence");
        Assert.Single(states.EnumerateArray()); Assert.False(states[0].GetProperty("presence").GetProperty("online").GetBoolean());
        Assert.Equal(a.Device, states[0].GetProperty("id").GetGuid());
    }
    [Fact]
    public async Task EmailLoginProfileRotationAndOrganizationProof()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory);
        var email = Guid.NewGuid().ToString("N").ToUpperInvariant() + "@EXAMPLE.INVALID";
        foreach (var org in new[] { a.Org, b.Org })
        {
            using var scope = factory.Services.CreateScope(); scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = org;
            var db = scope.ServiceProvider.GetRequiredService<Database>(); var user = await db.Users.SingleAsync(); user.Email = email;
            if (org == b.Org) user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, "Different-password-2!");
            await db.SaveChangesAsync();
        }
        using var client = Client(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password })).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me"); Assert.Equal(a.Org, me.GetProperty("organizationId").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/organizations", new { password = "wrong" })).StatusCode);
        var orgs = await (await client.PostAsJsonAsync("/api/v1/auth/organizations", new { password = Password })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(orgs.EnumerateArray()); Assert.Equal(a.Org, orgs[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(b.Org, email, Password, null))).StatusCode);
        using var otherSession = Client(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await otherSession.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, email, Password, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/auth/profile", new ProfileInput(email, Password, "1234567"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/v1/auth/profile", new ProfileInput(email, "wrong", "12345678"))).StatusCode);
        var newEmail = Guid.NewGuid().ToString("N").ToUpperInvariant() + "@EXAMPLE.INVALID";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/auth/profile", new ProfileInput(newEmail, Password, "12345678"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(newEmail, (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/profile")).GetProperty("email").GetString());
        using var fresh = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, newEmail, Password, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fresh.PostAsJsonAsync("/api/v1/auth/login", new { email = newEmail, password = "12345678" })).StatusCode);
    }
    [Fact]
    public async Task CorsPreflightAllowsOnlyConfiguredPanel()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        foreach (var origin in new[] { "https://localhost", "https://attacker.invalid" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,x-remvora-request");
            using var response = await client.SendAsync(request);
            if (origin == "https://localhost")
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
                Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            }
            else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }
    [Fact]
    public async Task GroupScopeProtectsListsDirectRoutesAndAdministrativeEscalation()
    {
        using var factory = new Factory(); var a = await Seed(factory);
        Guid groupId, outsideId;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org;
            var db = scope.ServiceProvider.GetRequiredService<Database>();
            var group = new DeviceGroup(a.Org, "Authorized group"); groupId = group.Id;
            var outside = new Device(a.Org, "outside"); outsideId = outside.Id;
            db.AddRange(group, outside, new DeviceLink(a.Org, a.Device, group: group.Id)); await db.SaveChangesAsync();
        }
        using var owner = Client(factory); await Login(owner, a.Org);
        var created = await owner.PostAsJsonAsync("/api/v1/users", new UserInput("scoped@example.invalid", Password, "Admin")); created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/users/{id}/device-groups", new { groupIds = new[] { groupId } })).EnsureSuccessStatusCode();
        using var scoped = Client(factory);
        (await scoped.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "scoped@example.invalid", Password, null))).EnsureSuccessStatusCode();
        var list = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/devices"); Assert.Single(list.EnumerateArray()); Assert.Equal(a.Device, list[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/v1/devices/{outsideId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.PatchAsJsonAsync($"/api/v1/devices/{outsideId}", new DeviceInput("denied", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.PostAsJsonAsync("/api/v1/devices", new DeviceInput("denied", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.PutAsJsonAsync($"/api/v1/devices/{a.Device}/assignments", new AssignmentInput(null, null, [], [], []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.PutAsJsonAsync($"/api/v1/users/{id}/device-groups", new { groupIds = (Guid[]?)null })).StatusCode);
        (await owner.PutAsJsonAsync($"/api/v1/users/{id}/device-groups", new { groupIds = Array.Empty<Guid>() })).EnsureSuccessStatusCode();
        Assert.Empty((await scoped.GetFromJsonAsync<JsonElement>("/api/v1/devices")).EnumerateArray());
        (await owner.PutAsJsonAsync($"/api/v1/users/{id}/device-groups", new { groupIds = (Guid[]?)null })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await scoped.GetFromJsonAsync<JsonElement>("/api/v1/devices")).GetArrayLength());
    }
    [Fact]
    public async Task DisabledDevicesCannotAuthenticateAndRevokedIdentityCannotBeReenabled()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org;
            var db = scope.ServiceProvider.GetRequiredService<Database>(); var device = await db.Devices.SingleAsync(x => x.Id == a.Device);
            device.RequestEnrollment("test-key", "test", "test", "1"); device.Activate(); await db.SaveChangesAsync();
        }
        using var client = Client(factory); await Login(client, a.Org);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/v1/devices/{b.Device}/status", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync($"/api/v1/devices/{a.Device}/status", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/agent/challenge", new { deviceId = a.Device, purpose = "connect" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync($"/api/v1/devices/{a.Device}/status", new { enabled = true })).StatusCode);
        (await client.DeleteAsync($"/api/v1/devices/{a.Device}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/devices/{a.Device}/status", new { enabled = true })).StatusCode);
    }
    [Fact]
    public async Task SessionsArePrivateAndSingleRevocationInvalidatesCookie()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory);
        using var first = Client(factory); using var second = Client(factory); using var other = Client(factory);
        await Login(first, a.Org); await Login(second, a.Org); await Login(other, b.Org);
        var sessions = await first.GetFromJsonAsync<JsonElement>("/api/v1/auth/sessions");
        Assert.Equal(2, sessions.GetArrayLength());
        var id = sessions.EnumerateArray().Single(x => !x.GetProperty("current").GetBoolean()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/auth/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/v1/auth/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Fact]
    public async Task PagesAreBoundedStableAndTenantScoped()
    {
        using var factory = new Factory(); var a = await Seed(factory); await Seed(factory);
        using var client = Client(factory); await Login(client, a.Org);
        (await client.PostAsJsonAsync("/api/v1/devices", new DeviceInput("test-device", null))).EnsureSuccessStatusCode();
        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/devices?limit=1&offset=0");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/v1/devices?limit=1&offset=1");
        Assert.Single(first.EnumerateArray()); Assert.Single(second.EnumerateArray());
        Assert.NotEqual(first[0].GetProperty("id").GetGuid(), second[0].GetProperty("id").GetGuid());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/v1/devices?limit=1&offset=2")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/devices?limit=501")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/audit?offset=-1")).StatusCode);
    }
    [Fact]
    public async Task RetentionPreservesActiveCredentialsAuditAndPendingApproval()
    {
        using var factory = new Factory(); var a = await Seed(factory);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<Database>();
        scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org;
        var old = new Challenge(a.Org, a.Device, "old", "connect", DateTimeOffset.UtcNow.AddDays(-2));
        var live = new Challenge(a.Org, a.Device, "live", "connect", DateTimeOffset.UtcNow.AddMinutes(1));
        var pending = await db.Devices.SingleAsync(x => x.Id == a.Device); pending.RequestEnrollment("test-key", "test", "test", "1");
        var enrollment = new Enrollment(a.Org, a.Device, Tokens.Hash(Tokens.New()), DateTimeOffset.UtcNow.AddDays(-2)) { ConsumedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var audit = new AuditEvent(a.Org, null, a.Device, "TEST", null, null);
        var abandoned = new RemoteSession(a.Org, Guid.NewGuid(), a.Device, SessionKind.Terminal, Tokens.Hash(Tokens.New())) { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-2), ConsumedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        db.AddRange(old, live, enrollment, audit, abandoned); await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<RetentionService>().Sweep(default);
        Assert.False(await db.Challenges.AnyAsync(x => x.Id == old.Id));
        Assert.False(await db.RemoteSessions.AnyAsync(x => x.Id == abandoned.Id));
        Assert.True(await db.Challenges.AnyAsync(x => x.Id == live.Id));
        Assert.True(await db.Enrollments.AnyAsync(x => x.Id == enrollment.Id));
        Assert.True(await db.Audit.AnyAsync(x => x.Id == audit.Id));
    }
    [Fact]
    public async Task OtherOrganizationDeviceIsNeverReturned()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory); using var client = Client(factory); await Login(client, a.Org);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/devices/{b.Device}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/v1/devices/{b.Device}", new DeviceInput("attack", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/devices/{b.Device}")).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/devices"); Assert.Single(list.EnumerateArray());
    }
    [Fact]
    public async Task ViewerCannotCreateOrEnroll()
    {
        using var factory = new Factory(); var a = await Seed(factory, "Viewer"); using var client = Client(factory); await Login(client, a.Org);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/devices", new DeviceInput("denied", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/devices/{a.Device}/enrollment", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/devices/{a.Device}/reboot", new { deviceCode = "invalid" })).StatusCode);
    }
    [Fact]
    public async Task CookieMutationsRequireExactOriginAndHeader()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var client = Client(factory); await Login(client, a.Org);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/devices", new DeviceInput("denied", null))).StatusCode);
    }
    [Fact]
    public async Task ApiKeysEnforceScopesAndRevocation()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var owner = Client(factory); await Login(owner, a.Org);
        var created = await owner.PostAsJsonAsync("/api/v1/api-keys", new ApiKeyInput("read", ["devices.read"], DateTimeOffset.UtcNow.AddHours(1)));
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var external = factory.CreateClient(); external.DefaultRequestHeaders.Authorization = new("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await external.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await external.PostAsJsonAsync("/api/v1/devices", new DeviceInput("denied", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await external.PostAsync($"/api/v1/devices/{a.Device}/enrollment", null)).StatusCode);
        var keys = await owner.GetFromJsonAsync<JsonElement>("/api/v1/api-keys"); var id = keys[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/api-keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await external.GetAsync("/api/v1/devices")).StatusCode);
    }
    [Fact]
    public async Task EnrollmentNeedsApprovalAndProofCannotBeReplayed()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var client = Client(factory); await Login(client, a.Org);
        var issued = await client.PostAsync($"/api/v1/devices/{a.Device}/enrollment", null);
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new EnrollmentInput(token, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "Linux", "arm64", "0.1.0");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/enrollment/request", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/enrollment/request", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/agent/challenge", new { deviceId = a.Device, purpose = "activate" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/devices/{a.Device}/approve", null)).StatusCode);
        var challengeResponse = await client.PostAsJsonAsync("/api/v1/agent/challenge", new { deviceId = a.Device, purpose = "activate" });
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var proof = new ProofInput(challenge.GetProperty("challengeId").GetGuid(), Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge.GetProperty("challenge").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/agent/activate", proof)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/agent/activate", proof)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/devices/{a.Device}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/agent/challenge", new { deviceId = a.Device, purpose = "connect" })).StatusCode);
    }
    [Fact]
    public async Task QueryFilterAndWriteGuardBlockCrossTenantAccess()
    {
        using var factory = new Factory(); var a = await Seed(factory); var b = await Seed(factory);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<Database>(); scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org;
        Assert.Null(await db.Devices.SingleOrDefaultAsync(x => x.Id == b.Device));
        db.Add(new Device(b.Org, "illegal")); await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task TwoFactorAndRecoveryCodesAreSingleUse()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var client = Client(factory); await Login(client, a.Org);
        var setup = await client.PostAsync("/api/v1/auth/totp/setup", null); var secret = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var ch in secret) { buffer = (buffer << 5) | alphabet.IndexOf(ch); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); } }
        var code = Totp.Generate(bytes.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var confirmed = await client.PostAsJsonAsync("/api/v1/auth/totp/confirm", new { code }); Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var recovery = (await confirmed.Content.ReadFromJsonAsync<string[]>())!;
        await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "test@example.invalid", Password, code))).StatusCode);
        await Login(client, a.Org, recovery[0]); await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "test@example.invalid", Password, recovery[0]))).StatusCode);
    }
    [Fact]
    public async Task RotatingSessionInvalidatesOldCookie()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var client = Client(factory);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "test@example.invalid", Password, null));
        var oldCookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/refresh", null)).StatusCode);
        using var stale = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") }); stale.DefaultRequestHeaders.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Fact]
    public async Task CustomRolesCannotEscalateAndLastOwnerIsPreserved()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var owner = Client(factory); await Login(owner, a.Org);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/v1/roles", new RoleInput("Inventory", ["devices.view"]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/users", new UserInput("reader@example.invalid", Password, "Inventory"))).StatusCode);
        var users = await owner.GetFromJsonAsync<JsonElement>("/api/v1/users"); var ownerId = users.EnumerateArray().Single(x => x.GetProperty("role").GetString() == "Owner").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/users/{ownerId}/role", new { role = "Viewer" })).StatusCode);
        using var reader = Client(factory); Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "reader@example.invalid", Password, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/roles", new RoleInput("Escalated", Permissions.All))).StatusCode);
    }
    [Fact]
    public async Task WebSocketSessionTicketIsBoundAndSingleUse()
    {
        using var factory = new Factory(); var a = await Seed(factory); using var client = Client(factory); await Login(client, a.Org);
        var operatorResponse = await client.PostAsJsonAsync("/api/v1/users", new UserInput("socket-operator@example.invalid", Password, "Operator"));
        operatorResponse.EnsureSuccessStatusCode(); var operatorId = (await operatorResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "socket-operator@example.invalid", Password, null))).EnsureSuccessStatusCode();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Actor>().OrganizationId = a.Org; var db = scope.ServiceProvider.GetRequiredService<Database>(); var device = await db.Devices.SingleAsync(x => x.Id == a.Device);
            device.RequestEnrollment(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "test", "arm64", "0.1.0"); device.Activate(); await db.SaveChangesAsync();
        }
        var challengeResponse = await client.PostAsJsonAsync("/api/v1/agent/challenge", new { deviceId = a.Device, purpose = "connect" });
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var proof = new ProofInput(challenge.GetProperty("challengeId").GetGuid(), Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge.GetProperty("challenge").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var agentClient = factory.Server.CreateWebSocketClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var agent = await agentClient.ConnectAsync(new Uri("wss://localhost/ws/agent"), timeout.Token); var agentPeer = new Peer(agent);
        await agentPeer.Send(Signal.Create("agent.authenticate", null, proof), timeout.Token);
        Assert.Equal("agent.connected", (await agentPeer.Receive(timeout.Token)).Type);
        var ticketResponse = await client.PostAsJsonAsync("/api/v1/remote-sessions", new SessionInput(a.Device, SessionKind.Terminal));
        Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode); var ticket = await ticketResponse.Content.ReadFromJsonAsync<JsonElement>(); var sessionId = ticket.GetProperty("sessionId").GetGuid();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginInput(a.Org, "socket-operator@example.invalid", Password, null)); var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var browserClient = factory.Server.CreateWebSocketClient(); browserClient.ConfigureRequest = request => { request.Headers["Origin"] = "https://localhost"; request.Headers["Cookie"] = cookie; };
        using var browser = await browserClient.ConnectAsync(new Uri("wss://localhost/ws/browser"), timeout.Token); var browserPeer = new Peer(browser);
        await browserPeer.Send(Signal.Create("session.request", sessionId, new { token = ticket.GetProperty("token").GetString() }), timeout.Token);
        var request = await agentPeer.Receive(timeout.Token); Assert.Equal("session.request", request.Type); Assert.Equal("Terminal", request.Payload.GetProperty("kind").GetString());
        await agentPeer.Send(Signal.Create("session.accept", sessionId, new { }), timeout.Token); Assert.Equal("session.accept", (await browserPeer.Receive(timeout.Token)).Type);
        await browserPeer.Send(Signal.Create("session.heartbeat", sessionId, new { }), timeout.Token);
        await browserPeer.Send(Signal.Create("relay.start", sessionId, new { }), timeout.Token);
        Assert.Equal("relay.start", (await agentPeer.Receive(timeout.Token)).Type);
        await agentPeer.Send(Signal.Create("relay.ready", sessionId, new { }), timeout.Token);
        Assert.Equal("relay.ready", (await browserPeer.Receive(timeout.Token)).Type);
        var input = new { channel = "input", data = "aGVsbG8=", text = false, part = 0, last = true, sequence = 1 };
        await browserPeer.Send(Signal.Create("relay.data", sessionId, input), timeout.Token);
        var forwarded = await agentPeer.Receive(timeout.Token);
        Assert.Equal("relay.data", forwarded.Type); Assert.Equal("aGVsbG8=", forwarded.Payload.GetProperty("data").GetString());
        await agentPeer.Send(Signal.Create("relay.data", sessionId, input), timeout.Token);
        Assert.Equal("relay.data", (await browserPeer.Receive(timeout.Token)).Type);
        using var replay = await browserClient.ConnectAsync(new Uri("wss://localhost/ws/browser"), timeout.Token); var replayPeer = new Peer(replay);
        await replayPeer.Send(Signal.Create("session.request", sessionId, new { token = ticket.GetProperty("token").GetString() }), timeout.Token);
        var rejected = await Record.ExceptionAsync(async () => await replayPeer.Receive(timeout.Token));
        Assert.NotNull(rejected); Assert.False(rejected is OperationCanceledException, "Replay must be actively rejected, not pass through a test timeout");
        using var administrator = Client(factory); await Login(administrator, a.Org);
        (await administrator.PutAsJsonAsync($"/api/v1/users/{operatorId}/device-groups", new { groupIds = Array.Empty<Guid>() })).EnsureSuccessStatusCode();
        using var revokeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var revoked = await Record.ExceptionAsync(async () => await browserPeer.Receive(revokeDeadline.Token));
        Assert.NotNull(revoked); Assert.False(revoked is OperationCanceledException, "Group scope revocation must terminate the live socket");
    }
    [Fact]
    public void TotpMatchesRfcVectorAndRejectsReplay()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890"); Assert.Equal("287082", Totp.Generate(secret, 1));
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30; var code = Totp.Generate(secret, step);
        Assert.Equal(step, Totp.Verify(secret, code, -1)); Assert.Equal(-1, Totp.Verify(secret, code, step));
    }
}
