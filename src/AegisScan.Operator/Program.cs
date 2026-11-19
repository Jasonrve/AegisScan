using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using k8s;
using KubeOps.Operator;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddKubernetesOperator(settings =>
{
    settings.Namespace = Environment.GetEnvironmentVariable("AEGIS_KUBE_NAMESPACE") ?? "default";
}).RegisterComponents();

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_000_000);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres (env ConnectionStrings__Postgres) must be set.");
builder.Services.AddDbContext<AegisDbContext>(options => options.UseNpgsql(connectionString));
// The DB check is tagged "ready" only: readiness should reflect dependency health so
// Kubernetes stops routing traffic here, but liveness must not — a DB blip would otherwise
// make Kubernetes kill and restart a perfectly healthy process in a loop.
builder.Services.AddHealthChecks().AddDbContextCheck<AegisDbContext>(tags: ["ready"]);

var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority (env Authentication__Authority) must be set.");
var audience = builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException("Authentication:Audience (env Authentication__Audience) must be set.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        // Compose runs Keycloak over plain HTTP on the docker network; a real deployment must front it with TLS.
        options.RequireHttpsMetadata = authority.StartsWith("https://", StringComparison.Ordinal);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            RoleClaimType = "groups",
            NameClaimType = "preferred_username"
        };
    });
builder.Services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleMappingClaimsTransformation>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AegisRoles.Viewer, policy => policy.RequireRole("admin", "operator", "viewer"))
    .AddPolicy(AegisRoles.Operator, policy => policy.RequireRole("admin", "operator"))
    .AddPolicy(AegisRoles.Admin, policy => policy.RequireRole("admin"));

builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig()));
builder.Services.AddHostedService<ScanExecutionWorker>();
builder.Services.AddHostedService<SchedulingWorker>();
// Retry-with-jittered-backoff + circuit breaker + timeout for all outbound calls to external
// services (GitHub discovery, webhook notifications) — one line via the standard resilience handler.
builder.Services.AddHttpClient("resilient").AddStandardResilienceHandler();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", context =>
    {
        var key = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            // The UI polls every 5s (~40 requests/min per open tab, keyed by user), so 100/min made multi-tab users hit 429.
            PermitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimitPerMinute", 300),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var migrated = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
    await migrated.Database.MigrateAsync();
    if (Environment.GetEnvironmentVariable("AEGIS_SEED_DEMO") == "true") DemoData.Seed(migrated);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
    await next();
});

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Http").BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseHttpMetrics();

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = check => !check.Tags.Contains("ready") });
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapMetrics();
app.MapAegisApi();

Metrics.CreateGauge("aegisscan_up", "Operator process availability.").Set(1);

await app.RunAsync();
