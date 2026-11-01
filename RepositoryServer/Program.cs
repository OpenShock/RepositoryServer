using System.Net;
using Asp.Versioning;
using EntityFramework.Exceptions.PostgreSQL;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BlazorBlueprint.Components;
using OpenShock.Internal.Common.ExceptionHandling;
using OpenShock.Internal.Common.Utils;
using OpenShock.RepositoryServer;
using OpenShock.RepositoryServer.AuthenticationHandlers;
using OpenShock.RepositoryServer.Components;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Services;
using OpenShock.RepositoryServer.Services.Admin;
using OpenShock.RepositoryServer.Utils;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;
using Serilog;
using ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;
using JsonOptions = OpenShock.RepositoryServer.JsonSerialization.JsonOptions;
using ValidationProblem = OpenShock.RepositoryServer.Problems.ValidationProblem;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Custom.json", optional: true, reloadOnChange: false)
    .AddUserSecrets<Program>(true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var isDevelopment = builder.Environment.IsDevelopment();
builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateScopes = isDevelopment;
    options.ValidateOnBuild = isDevelopment;
});

// Since we use slim builders, this allows for HTTPS
builder.WebHost.UseKestrelHttpsConfiguration();

// Also absent from the slim builder. Without it the _content/** assets that packages contribute,
// Blazor Blueprint's stylesheet among them, are not on disk next to the app during development and
// the admin UI renders unstyled.
builder.WebHost.UseStaticWebAssets();

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMilliseconds(3000);
});

builder.Host.UseSerilog((context, _, config) => config.ReadFrom.Configuration(context.Configuration));

var config = builder.GetAndRegisterOpenShockConfig<ApiConfig>();

// <---- ASP.NET ---->
// The shared handler takes its serializer options directly rather than through DI, so it is
// constructed by hand. AddExceptionHandler<T>() is just this singleton registration underneath.
builder.Services.AddSingleton<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>(sp =>
    new OpenShockExceptionHandler(
        sp.GetRequiredService<IHostEnvironment>(),
        sp.GetRequiredService<ILoggerFactory>(),
        JsonOptions.Default));

// The admin bypass exists only in Debug builds, only in Development, and only when asked for.
// Release builds do not contain the handler at all, so the published image cannot be talked into it.
#if DEBUG
var devAdminBypass = isDevelopment && config.DevAuth.BypassLogin;
#else
const bool devAdminBypass = false;
#endif

if (!devAdminBypass && config.GitHub is null)
{
    Console.WriteLine("Error validating config: the GitHub section is required.");
    Console.WriteLine("Admin endpoints have no other credential, so a server without it cannot be administered.");
    Console.WriteLine("For local development, run in the Development environment with DevAuth:BypassLogin=true.");
    Environment.Exit(-10);
}

if (devAdminBypass)
{
    Console.WriteLine("###############################################################");
    Console.WriteLine("# LOGIN BYPASS ACTIVE. Every caller is an administrator.      #");
    Console.WriteLine("# Development builds only. Never expose this to a network.    #");
    Console.WriteLine("###############################################################");
}

builder.Services.AddSingleton(new AdminAuthMode(devAdminBypass));

// Only the default scheme is authenticated by UseAuthentication, and with three registered there was
// no default at all, so HttpContext.User was anonymous on any route without an authorization policy.
// The landing page needs it: it is anonymous by necessity and still has to tell a live session from
// none. Authenticate only — challenge and sign-out stay explicit at their call sites, and the gated
// endpoints are untouched because their policies name the schemes they accept.
var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = AuthSchemas.AdminCookie;
});

if (devAdminBypass)
{
#if DEBUG
    // Stands in for the cookie scheme rather than adding another, so the policy, the endpoints and
    // the claims they read stay exactly as they are in production.
    authenticationBuilder.AddScheme<AuthenticationSchemeOptions, DevAdminAuthentication>(
        AuthSchemas.AdminCookie, _ => { });
#endif
}
else
{
    var github = config.GitHub!;

    authenticationBuilder
        .AddCookie(AuthSchemas.AdminCookie, options =>
        {
            GitHubAuthentication.ConfigureCookie(options, github);
        })
        .AddGitHub(AuthSchemas.AdminOAuth, options =>
        {
            GitHubAuthentication.ConfigureOAuth(options, github);
        });
    
        builder.Services.AddDataProtection().PersistKeysToDbContext<RepoServerContext>();
}

authenticationBuilder.AddJwtBearer(AuthSchemas.CiCdToken, options =>
{
    GitHubOidcAuthentication.Configure(options, config.CiCd.Audience);
});

var adminTeam = config.GitHub?.Team ?? AdminAuthMode.DevFallbackTeam;

builder.Services.AddAuthorizationBuilder()
    // Admin endpoints authorize against the session cookie, never against the OAuth scheme: by the
    // time a request carries a session, GitHub's part is finished.
    .AddPolicy(AuthSchemas.Policies.Admin, policy => policy
        .AddAuthenticationSchemes(AuthSchemas.AdminCookie)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthSchemas.AdminClaims.Team, adminTeam))
    // Firmware and desktop ingestion share the CI/CD scheme, so being authenticated is not enough:
    // each endpoint requires the scope its grant was issued for.
    .AddPolicy(AuthSchemas.Policies.PublishFirmware, policy => policy
        .AddAuthenticationSchemes(AuthSchemas.CiCdToken)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthSchemas.CiCdClaims.Scope, RepositoryScope.PublishFirmware.ToScopeClaim()))
    .AddPolicy(AuthSchemas.Policies.PublishModules, policy => policy
        .AddAuthenticationSchemes(AuthSchemas.CiCdToken)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthSchemas.CiCdClaims.Scope, RepositoryScope.PublishModules.ToScopeClaim()));


// Authorization failures on the API surface answer with a problem body naming the unmet
// requirements, rather than the framework's bare 403.
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, OpenShockAuthorizationMiddlewareResultHandler>();

builder.Services.ConfigureHttpJsonOptions(options => JsonOptions.ConfigureDefault(options.SerializerOptions));

// Admin UI. Interactive server rendering: the pages call the admin services and EF Core directly,
// and there is no admin HTTP API for a client-side runtime to talk to. Pages carry the admin policy
// as endpoint metadata, so the gate closes before anything renders.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddBlazorBlueprintComponents();

builder.Services.AddControllers().AddJsonOptions(x => JsonOptions.ConfigureDefault(x.JsonSerializerOptions));

builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
    })
    // Controller-based APIs need the MVC integration to be versioned at all; without it the
    // [ApiVersion] attributes are inert and every action lands in the default version.
    .AddMvc()
    .AddApiExplorer(setup =>
    {
        setup.GroupNameFormat = "VVV";
        setup.SubstituteApiVersionInUrl = true;
        setup.DefaultApiVersion = new ApiVersion(1, 0);
        setup.AssumeDefaultVersionWhenUnspecified = true;
    })
    // Registers one OpenAPI document per discovered API version, rather than the single hand-named
    // document this used to declare, which described v1 and left every v2 endpoint undocumented.
    .AddOpenApi();

// The OAuth redirect_uri is built from the incoming request, so behind an ingress that terminates
// TLS the server would otherwise send GitHub an http:// callback that does not match the one
// registered on the OAuth app, and the login fails at the last hop.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                               ForwardedHeaders.XForwardedHost;

    // Only an in-cluster ingress is trusted to rewrite these. Accepting them from anywhere would let
    // a caller claim any client IP, which the metrics endpoint uses for access control.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in TrustedProxiesFetcher.PrivateNetworks)
    {
        options.KnownIPNetworks.Add(IPNetwork.Parse(network));
    }
});

// generic ASP.NET stuff
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddWebEncoders();
builder.Services.AddProblemDetails();
builder.Services.TryAddSingleton<TimeProvider>(provider => TimeProvider.System);

// Any origin may read the public firmware catalog: the flashtool and CLI tools are cross-origin by
// nature and the data is public anyway.
//
// Credentials are deliberately NOT allowed. Admin auth is a session cookie, and reflecting arbitrary
// origins while allowing credentials would let any page a logged-in admin visits call the admin
// endpoints with their session attached. The admin UI is served from this origin, so it never needs
// CORS at all.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(corsPolicyBuilder =>
    {
        corsPolicyBuilder.AllowAnyOrigin();
        corsPolicyBuilder.AllowAnyHeader();
        corsPolicyBuilder.AllowAnyMethod();
        corsPolicyBuilder.SetPreflightMaxAge(TimeSpan.FromHours(24));
    });
});

// This needs to be at this position, earlier will break validation error responses
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problemDetails = new ValidationProblem(context.ModelState);
        return problemDetails.ToObjectResult(context.HttpContext);
    };
});

// OpenTelemetry

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddRuntimeInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter());

// <---- Storage Service ---->
switch (config.Firmware.Storage.Type)
{
    case StorageType.BunnyCdn:
        var bunnyCdnConfig = config.Firmware.Storage.BunnyCdn
                             ?? throw new InvalidOperationException("BunnyCdn storage config is required when Type is BunnyCdn.");
        builder.Services.AddSingleton(bunnyCdnConfig);
        builder.Services.AddHttpClient<BunnyCdnStorageService>();
        builder.Services.AddSingleton<IStorageService>(sp => sp.GetRequiredService<BunnyCdnStorageService>());
        break;

    case StorageType.Local:
        var localConfig = config.Firmware.Storage.Local
                          ?? throw new InvalidOperationException("Local storage config is required when Type is Local.");
        builder.Services.AddSingleton(localConfig);
        builder.Services.AddSingleton<IStorageService, LocalStorageService>();
        break;

    case StorageType.S3:
        var s3Config = config.Firmware.Storage.S3
                       ?? throw new InvalidOperationException("S3 storage config is required when Type is S3.");
        builder.Services.AddSingleton(s3Config);
        builder.Services.AddSingleton<IStorageService, S3StorageService>();
        break;

    default:
        throw new InvalidOperationException($"Unknown storage type: {config.Firmware.Storage.Type}");
}

// <---- Discord notifications ---->
// Notifications outlive the request that triggers them, so the service is a singleton that resolves
// its own HttpClient and DbContext rather than capturing request-scoped ones.
builder.Services.AddHttpClient(nameof(DiscordNotificationService));
builder.Services.AddSingleton<IDiscordNotificationService, DiscordNotificationService>();

// <---- Admin services ---->
// All administration goes through these. The management UI is their only caller today; a
// token-authenticated automation API would sit on top of the same services rather than beside them.
builder.Services.AddScoped<AdminOverviewService>();
builder.Services.AddScoped<CatalogAdminService>();
builder.Services.AddScoped<PublisherAdminService>();
builder.Services.AddScoped<AdvisoryAdminService>();
builder.Services.AddScoped<DiscordWebhookAdminService>();
builder.Services.AddScoped<ReleaseAdminService>();
builder.Services.AddScoped<ModuleAdminService>();

// Composes the services above rather than writing itself, so a bulk import is held to the same
// invariants as the equivalent clicking.
builder.Services.AddScoped<CatalogImportService>();

// Typed client: the publishers page offers an owner's public repositories to pick from. The call
// carries no credential - listing public repositories needs none - so this widens nothing about the
// admin login, and the timeout keeps a slow GitHub from holding the dialog open.
builder.Services.AddSingleton(new GitHubOwnerDefaults(config.GitHub?.Organization));
builder.Services.AddHttpClient<GitHubPublicRepositoryService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(10);

    // GitHub answers 403 to a request without one.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenShock.RepositoryServer");
});

// Typed client: publish probes the artifact URLs it is about to advertise, and a hostname that
// black-holes must fail the publish quickly rather than hold the request open.
builder.Services.AddHttpClient<PublishedArtifactVerifier>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

// <---- Background cleanup ---->
builder.Services.AddHostedService<StagedReleaseCleanupService>();

// <---- Postgres EF Core ---->

// Register the pooled factory only and derive scoped contexts from it. Adding
// AddDbContextPool alongside would apply a second options configuration to the same
// options builder (EF applies all registered configurations cumulatively), duplicating
// the Npgsql enum definitions and breaking type mapping.
builder.Services.AddPooledDbContextFactory<RepoServerContext>(dbBuilder =>
{
    RepoServerContext.ConfigureOptionsBuilder(dbBuilder, config.Db.Conn, config.Db.Debug);
});
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<RepoServerContext>>().CreateDbContext());



var app = builder.Build();

if (!config.Db.SkipMigration)
{
    Log.Information("Running database migrations...");
    using var scope = app.Services.CreateScope();

    await using var migrationContext = new MigrationOpenShockContext(
        config.Db.Conn,
        config.Db.Debug,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
    var pendingMigrations = migrationContext.Database.GetPendingMigrations().ToArray();

    if (pendingMigrations.Length > 0)
    {
        Log.Information("Found pending migrations, applying [{@Migrations}]", pendingMigrations);
        migrationContext.Database.Migrate();
        Log.Information("Applied database migrations... proceeding with startup");
    }
    else
    {
        Log.Information("No pending migrations found, proceeding with startup");
    }
}
else
{
    Log.Warning("Skipping possible database migrations...");
}

// Must run before anything reads the scheme, host or client IP, which means before request logging.
app.UseForwardedHeaders();

app.UseSerilogRequestLogging();

// Enable request body buffering. Needed to allow rewinding the body reader,
// if the body has already been read before.
// Runs before the request action is executed and body is read.
app.Use((context, next) =>
{
    context.Request.EnableBuffering();
    return next.Invoke();
});
app.UseExceptionHandler();

// global cors policy
app.UseCors();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(1)
});
app.UseRouting();

// Serves blazor.web.js and Blazor Blueprint's bundled css/js out of the packages' static web assets.
// Deliberately the middleware rather than MapStaticAssets(): the endpoint-routing variant needs the
// build-time asset manifest, which resolves against the content root, and under
// WebApplicationFactory that is the test project. It came up empty there and swallowed every
// controller route, 404ing the whole API. This has no manifest to miss.
//
// Ahead of authentication on purpose. These are framework files, identical for every deployment,
// and the admin pages need their stylesheet before there is a session to check.
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

var metricsAllowedIpNetworks = config.Metrics.AllowedNetworks.Select(IPNetwork.Parse);

app.UseOpenTelemetryPrometheusScrapingEndpoint(context =>
{
    if(context.Request.Path != "/metrics") return false;

    var remoteIp = context.Connection.RemoteIpAddress;
    return remoteIp != null && metricsAllowedIpNetworks.Any(x => x.Contains(remoteIp));
});

// Required by the Razor Components endpoints. Cookie-authenticated state changes need it, and
// MapRazorComponents refuses to serve without the middleware present.
app.UseAntiforgery();

app.MapOpenApi().WithDocumentPerVersion();
app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// One entry per API version, matching the documents WithDocumentPerVersion serves.
app.MapScalarApiReference(options =>
{
    options.AddDocument("1");
    options.AddDocument("2");
});

app.Run();
