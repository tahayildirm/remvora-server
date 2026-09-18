using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Remvora.Application;
using Remvora.Domain;
using Remvora.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(x => x.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOptions<SecurityPolicy>().Bind(builder.Configuration.GetSection("Security:Policy")).Validate(x => x.IsValid(), "Security policy lifetimes are outside permitted ranges").ValidateOnStart();
builder.Services.AddScoped<Actor>();
var provider = builder.Configuration["Database:Provider"] ?? "PostgreSQL";
var connection = builder.Configuration.GetConnectionString("Database");
switch (provider)
{
    case "PostgreSQL":
        builder.Services.AddDbContext<PostgresDatabase>(x => x.UseNpgsql(connection ?? "Host=localhost;Database=remvora;Username=remvora"));
        builder.Services.AddScoped<Database>(x => x.GetRequiredService<PostgresDatabase>()); break;
    case "MySQL":
        builder.Services.AddDbContext<MySqlDatabase>(x => x.UseMySql(connection ?? "Server=localhost;Database=remvora;User=remvora", new MySqlServerVersion(Version.Parse(builder.Configuration["Database:ServerVersion"] ?? "8.4.0"))));
        builder.Services.AddScoped<Database>(x => x.GetRequiredService<MySqlDatabase>()); break;
    case "MariaDB":
        builder.Services.AddDbContext<MariaDbDatabase>(x => x.UseMySql(connection ?? "Server=localhost;Database=remvora;User=remvora", new MariaDbServerVersion(Version.Parse(builder.Configuration["Database:ServerVersion"] ?? "11.4.0"))));
        builder.Services.AddScoped<Database>(x => x.GetRequiredService<MariaDbDatabase>()); break;
    default: throw new InvalidOperationException("Database:Provider must be PostgreSQL, MySQL or MariaDB");
}
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>(); builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<UserAdministration>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddScoped<AuthenticationService>(); builder.Services.AddScoped<EnrollmentService>(); builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.Configure<PasswordHasherOptions>(x => x.IterationCount = 210_000);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Remvora")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Security:KeyRingPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".runtime", "keys")));
if (builder.Configuration["Security:KeyRingCertificate"] is string certificatePath)
    dataProtection.ProtectKeysWithCertificate(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certificatePath, builder.Configuration["Security:KeyRingPassword"], System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.MachineKeySet));
else if (builder.Environment.IsProduction() && !args.Contains("--migrate") && !args.Contains("--bootstrap"))
    throw new InvalidOperationException("Production requires Security:KeyRingCertificate and a persisted key ring");
builder.Services.AddCors(options => options.AddPolicy("Panel", policy => policy
    .WithOrigins(builder.Configuration["Security:AllowedOrigin"] ?? "https://localhost:4200")
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD")
    .WithHeaders("Content-Type", "X-Remvora-Request", "Authorization")
    .AllowCredentials()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton<SignalingHub>();
builder.Services.AddRateLimiter(x =>
{
    x.RejectionStatusCode = 429;
    x.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    x.AddConcurrencyLimiter("socket", options => { options.PermitLimit = 1024; options.QueueLimit = 0; });
    x.AddFixedWindowLimiter("login", options => { options.PermitLimit = 30; options.Window = TimeSpan.FromMinutes(1); options.QueueLimit = 0; });
});
var app = builder.Build();
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<Database>().Database.MigrateAsync(); return;
}
if (args.Contains("--bootstrap"))
{
    using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<Database>();
    var actor = scope.ServiceProvider.GetRequiredService<Actor>();
    var email = Environment.GetEnvironmentVariable("REMVORA_ADMIN_EMAIL") ?? throw new InvalidOperationException("REMVORA_ADMIN_EMAIL required");
    var password = Environment.GetEnvironmentVariable("REMVORA_ADMIN_PASSWORD") ?? throw new InvalidOperationException("REMVORA_ADMIN_PASSWORD required");
    if (password.Length < 8 || password.Length > 256) throw new InvalidOperationException("Password length must be 8..256");
    if (await db.Organizations.AnyAsync()) throw new InvalidOperationException("Bootstrap only supports an empty installation");
    var organization = new Organization { Name = Environment.GetEnvironmentVariable("REMVORA_ORGANIZATION") ?? "Remvora" }; actor.OrganizationId = organization.Id;
    var user = new User(organization.Id, email.Trim().ToUpperInvariant(), "") { Role = "Owner" };
    user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, password);
    db.Add(organization); db.Add(user); await db.SaveChangesAsync(); Console.WriteLine($"Organization: {organization.Id}"); return;
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-Correlation-Id"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    try { await next(context); }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        var code = ex switch { DomainException domain => domain.Code, DbUpdateConcurrencyException => "CONCURRENT_CHANGE", DbUpdateException => "DATA_CONFLICT", JsonException or BadHttpRequestException => "INPUT_INVALID", _ => "INTERNAL_ERROR" };
        context.Response.StatusCode = code switch { "NOT_FOUND" => 404, "FORBIDDEN" => 403, "LOGIN_FAILED" or "UNAUTHORIZED" => 401, "CONCURRENT_CHANGE" or "DATA_CONFLICT" => 409, "INTERNAL_ERROR" => 500, _ => 400 };
        // Log codes only: database exception text can include parameters and private fields.
        app.Logger.LogWarning("Request failed with {ErrorCode}; correlation {CorrelationId}", code, context.TraceIdentifier);
        await context.Response.WriteAsJsonAsync(new { code, message = code == "INTERNAL_ERROR" ? "An internal error occurred." : "Request rejected.", correlationId = context.TraceIdentifier });
    }
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseCors("Panel");
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/health", StringComparison.Ordinal) || path.StartsWith("/openapi/", StringComparison.Ordinal)) { await next(context); return; }
    var actor = context.RequestServices.GetRequiredService<Actor>(); actor.Ip = context.Connection.RemoteIpAddress?.ToString(); actor.UserAgent = context.Request.Headers.UserAgent.ToString()[..Math.Min(context.Request.Headers.UserAgent.ToString().Length, 512)];
    var publicAgent = path is "/api/v1/enrollment/request" or "/api/v1/agent/challenge" or "/api/v1/agent/activate" or "/ws/agent";
    if (publicAgent) { await next(context); return; }
    var origin = builder.Configuration["Security:AllowedOrigin"] ?? "https://localhost:4200";
    var authorization = context.Request.Headers.Authorization.ToString();
    var apiToken = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : null;
    var mutating = context.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
    if ((mutating && apiToken is null) || context.WebSockets.IsWebSocketRequest)
    {
        if (context.Request.Headers.Origin != origin || (mutating && context.Request.Headers["X-Remvora-Request"] != "1")) throw new DomainException("FORBIDDEN");
    }
    if (path == "/api/v1/auth/login") { await next(context); return; }
    if (!await context.RequestServices.GetRequiredService<AuthenticationService>().Resolve(context.Request.Cookies["__Host-remvora"], apiToken, context.RequestAborted)) throw new DomainException("UNAUTHORIZED");
    if (actor.IsApiKey && !path.StartsWith("/api/v1/devices", StringComparison.Ordinal)) throw new DomainException("FORBIDDEN");
    await next(context);
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (Database db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
if (app.Environment.IsDevelopment()) app.MapOpenApi();
static void SetCookie(HttpContext context, string token) => context.Response.Cookies.Append("__Host-remvora", token,
    new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityPolicy>>().Value.UserSessionMinutes), IsEssential = true });
app.MapPost("/api/v1/auth/login", async (LoginInput input, AuthenticationService auth, HttpContext context, CancellationToken ct) =>
{
    if (input.Password.Length > 256 || input.Email.Length > 254) throw new DomainException("LOGIN_FAILED");
    SetCookie(context, await auth.Login(input, ct)); return Results.NoContent();
}).RequireRateLimiting("login");
app.MapGet("/api/v1/configuration", () => new { stunServers = builder.Configuration.GetSection("WebRtc:StunServers").Get<string[]>() ?? [] });
app.MapGet("/api/v1/auth/me", (Actor actor) => new { actor.OrganizationId, actor.UserId, actor.Role, Permissions = actor.Permissions.Where(actor.Can), actor.DeviceGroups });
app.MapGet("/api/v1/auth/profile", (AuthenticationService auth, CancellationToken ct) => auth.Profile(ct));
app.MapPost("/api/v1/auth/organizations", (PasswordInput input, AuthenticationService auth, CancellationToken ct) => auth.Organizations(input.Password, ct)).RequireRateLimiting("login");
app.MapPut("/api/v1/auth/profile", async (ProfileInput input, AuthenticationService auth, HttpContext context, CancellationToken ct) => { SetCookie(context, await auth.UpdateProfile(input, ct)); return Results.NoContent(); }).RequireRateLimiting("login");
app.MapPost("/api/v1/auth/refresh", async (AuthenticationService auth, HttpContext context, CancellationToken ct) => { SetCookie(context, await auth.Rotate(ct)); return Results.NoContent(); });
app.MapPost("/api/v1/auth/logout", async (AuthenticationService auth, HttpContext context, bool? all, CancellationToken ct) => { await auth.Logout(all ?? false, ct); context.Response.Cookies.Delete("__Host-remvora", new CookieOptions { Secure = true, Path = "/" }); return Results.NoContent(); });
app.MapGet("/api/v1/auth/sessions", (AuthenticationService auth, CancellationToken ct) => auth.Sessions(ct));
app.MapDelete("/api/v1/auth/sessions/{id:guid}", async (Guid id, AuthenticationService auth, CancellationToken ct) => { await auth.RevokeSession(id, ct); return Results.NoContent(); });
app.MapPost("/api/v1/auth/totp/setup", (AuthenticationService auth, CancellationToken ct) => auth.BeginTotp(ct));
app.MapPost("/api/v1/auth/totp/confirm", (CodeInput input, AuthenticationService auth, CancellationToken ct) => auth.ConfirmTotp(input.Code, ct));
app.MapGet("/api/v1/devices", (DeviceService service, int? offset, int? limit, CancellationToken ct) => service.List(ct, offset ?? 0, limit ?? 100));
app.MapGet("/api/v1/devices/{id:guid}", (Guid id, DeviceService service, CancellationToken ct) => service.Get(id, ct));
app.MapPost("/api/v1/devices", async (DeviceInput input, DeviceService service, CancellationToken ct) => { var device = await service.Create(input, ct); return Results.Created($"/api/v1/devices/{device.Id}", device); });
app.MapPatch("/api/v1/devices/{id:guid}", (Guid id, DeviceInput input, DeviceService service, CancellationToken ct) => service.Update(id, input, ct));
app.MapPatch("/api/v1/devices/{id:guid}/status", async (Guid id, DeviceStatusInput input, DeviceService service, CancellationToken ct) => { await service.SetEnabled(id, input.Enabled, ct); return Results.NoContent(); });
app.MapDelete("/api/v1/devices/{id:guid}", async (Guid id, DeviceService service, CancellationToken ct) => { await service.Revoke(id, ct); return Results.NoContent(); });
app.MapPost("/api/v1/devices/{id:guid}/delete-permanently", async (Guid id, PermanentDeleteInput input, DeviceService service, SignalingHub hub, CancellationToken ct) =>
{
    await service.DeletePermanently(id, input.Confirmation, ct);
    hub.DisconnectDevice(id);
    return Results.NoContent();
});
app.MapGet("/api/v1/devices/{id:guid}/assignments", async (Guid id, DeviceService service, Database db, CancellationToken ct) => { await service.Get(id, ct); return await db.Set<DeviceLink>().AsNoTracking().Where(x => x.DeviceId == id).ToListAsync(ct); });
app.MapPut("/api/v1/devices/{id:guid}/assignments", async (Guid id, AssignmentInput input, CatalogService service, CancellationToken ct) => { await service.Assign(id, input, ct); return Results.NoContent(); });
app.MapGet("/api/v1/catalog/{kind}", (string kind, CatalogService service, int? offset, int? limit, CancellationToken ct) => service.List(kind, ct, offset ?? 0, limit ?? 100));
app.MapPost("/api/v1/catalog/{kind}", (string kind, CatalogInput input, CatalogService service, CancellationToken ct) => service.Save(kind, null, input, ct));
app.MapPut("/api/v1/catalog/{kind}/{id:guid}", (string kind, Guid id, CatalogInput input, CatalogService service, CancellationToken ct) => service.Save(kind, id, input, ct));
app.MapDelete("/api/v1/catalog/{kind}/{id:guid}", async (string kind, Guid id, CatalogService service, CancellationToken ct) => { await service.Delete(kind, id, ct); return Results.NoContent(); });
app.MapPost("/api/v1/api-keys", async (ApiKeyInput input, AuthenticationService auth, CancellationToken ct) => new { token = await auth.CreateApiKey(input, ct) });
app.MapGet("/api/v1/api-keys", async (Actor actor, Database db, int? offset, int? limit, CancellationToken ct) => { actor.Require("apiKeys.manage"); var page = new PageRequest(offset ?? 0, limit ?? 100); return await db.ApiKeys.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).Select(x => new { x.Id, x.Name, x.Scopes, x.ExpiresAt, x.RevokedAt, x.LastUsedAt }).ToListAsync(ct); });
app.MapDelete("/api/v1/api-keys/{id:guid}", async (Guid id, AuthenticationService auth, CancellationToken ct) => { await auth.RevokeApiKey(id, ct); return Results.NoContent(); });
app.MapPost("/api/v1/devices/{id:guid}/enrollment", (Guid id, EnrollmentService service, CancellationToken ct) => service.Issue(id, ct));
app.MapPost("/api/v1/devices/{id:guid}/approve", async (Guid id, EnrollmentService service, CancellationToken ct) => { await service.Approve(id, ct); return Results.NoContent(); });
app.MapPost("/api/v1/enrollment/request", (EnrollmentInput input, EnrollmentService service, CancellationToken ct) => service.Request(input, ct));
app.MapPost("/api/v1/agent/challenge", (ChallengeInput input, EnrollmentService service, CancellationToken ct) => service.Challenge(input.DeviceId, input.Purpose, ct));
app.MapPost("/api/v1/agent/activate", async (ProofInput input, EnrollmentService service, CancellationToken ct) => { await service.Verify(input, "activate", ct); return Results.NoContent(); });
app.MapGet("/api/v1/users", (UserAdministration service, int? offset, int? limit, CancellationToken ct) => service.Users(ct, offset ?? 0, limit ?? 100));
app.MapPost("/api/v1/users", (UserInput input, UserAdministration service, CancellationToken ct) => service.Create(input, ct));
app.MapPut("/api/v1/users/{id:guid}", async (Guid id, UserUpdateInput input, UserAdministration service, CancellationToken ct) => { await service.Update(id, input, ct); return Results.NoContent(); });
app.MapPatch("/api/v1/users/{id:guid}/role", async (Guid id, RoleChange input, UserAdministration service, CancellationToken ct) => { await service.ChangeRole(id, input.Role, ct); return Results.NoContent(); });
app.MapPut("/api/v1/users/{id:guid}/device-groups", async (Guid id, DeviceGroupScopeInput input, UserAdministration service, CancellationToken ct) => { await service.SetDeviceGroups(id, input.GroupIds, ct); return Results.NoContent(); });
app.MapGet("/api/v1/roles", (UserAdministration service, CancellationToken ct) => service.Roles(ct));
app.MapPost("/api/v1/roles", async (RoleInput input, UserAdministration service, CancellationToken ct) => { await service.CreateRole(input, ct); return Results.NoContent(); });
app.MapGet("/api/v1/audit", async (Actor actor, Database db, int? offset, int? limit, CancellationToken ct) => { actor.Require("audit.view"); var page = new PageRequest(offset ?? 0, limit ?? 100); return await db.Audit.AsNoTracking().OrderByDescending(x => x.Timestamp).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit).ToListAsync(ct); });
app.MapPost("/api/v1/remote-sessions", (SessionInput input, SignalingHub hub, Database db, Actor actor, CancellationToken ct) => hub.Authorize(input, db, actor, ct));
app.MapPost("/api/v1/devices/{id:guid}/reboot", (Guid id, RebootInput input, SignalingHub hub, Database db, Actor actor, CancellationToken ct) => hub.RequestReboot(id, input.DeviceCode, db, actor, ct));
app.MapGet("/api/v1/devices/presence", async (DeviceService service, SignalingHub hub, int? offset, int? limit, CancellationToken ct) =>
{
    var devices = await service.List(ct, offset ?? 0, limit ?? 100);
    return devices.Select(device => new { device.Id, presence = hub.Presence(device.Id) });
});
app.MapGet("/api/v1/devices/{id:guid}/presence", async (Guid id, DeviceService service, SignalingHub hub, CancellationToken ct) => { await service.Get(id, ct); return hub.Presence(id); });
app.Map("/ws/agent", (HttpContext context, SignalingHub hub) => hub.Agent(context)).RequireRateLimiting("socket");
app.Map("/ws/browser", (HttpContext context, SignalingHub hub, Database db, Actor actor) => hub.Browser(context, db, actor)).RequireRateLimiting("socket");
app.Run();
public sealed record CodeInput(string Code);
public sealed record ChallengeInput(Guid DeviceId, string Purpose);
public partial class Program { }

public sealed record RoleChange(string Role);

public sealed record DeviceStatusInput(bool Enabled);

public sealed record DeviceGroupScopeInput(Guid[]? GroupIds);

public sealed record RebootInput(string DeviceCode);
