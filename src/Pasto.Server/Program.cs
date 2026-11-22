using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Npgsql;
using Pasto.Server.Api;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Pasto.Server.Observability;
using Serilog;

var migrate = args is ["--migrate"];
var builder = WebApplication.CreateBuilder(migrate ? [] : args);
builder.Logging.ClearProviders();
builder.Services.AddSerilog((_, logger) => ServerLogging.Configure(builder.Configuration, builder.Environment, logger),
    preserveStaticLogger: true, writeToProviders: true);
builder.AddPastoTelemetry();
// 在 JSON 綁定／字串配置前由 Kestrel 限制實際 body bytes，包含未知長度的串流。
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 32 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.MinResponseDataRate = new MinDataRate(8 * 1024, TimeSpan.FromSeconds(15));
});

// 連線由環境變數 ConnectionStrings__Pasto 或 .NET configuration 提供；不在 repo 保存密碼。
// Compose 的 .env 只供 Compose 使用，不會自動匯入 ASP.NET Core 的設定。
var deployed = builder.Configuration.GetValue<bool>("Deployment:Enabled");
var connectionString = deployed || migrate
    ? DeploymentDatabase.GetConnectionString(builder.Configuration, migrate)
    : builder.Configuration.GetConnectionString("Pasto");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings__Pasto before starting Pasto.Server.");
}

// Factory 讓每次建房嘗試擁有自己的 context；也會註冊 scoped PastoDbContext 供後續 use case 使用。
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration("Database")
    .Validate(options => options.MaxPoolSize is >= 1 and <= 256, "MaxPoolSize must be between 1 and 256.").ValidateOnStart();
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
{
    var settings = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    return CreateDataSource(connectionString, settings.MaxPoolSize, pooling: !migrate);
});
builder.Services.AddDbContextFactory<PastoDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddOptions<RoomCleanupOptions>().BindConfiguration("Cleanup")
    .Validate(options => options.MaxPoolSize is >= 1 and <= 4, "Cleanup MaxPoolSize must be between 1 and 4.")
    .ValidateOnStart();
builder.Services.AddKeyedSingleton<NpgsqlDataSource>("RoomCleanup", (services, _) =>
{
    var settings = services.GetRequiredService<IOptions<RoomCleanupOptions>>().Value;
    return CreateDataSource(connectionString, settings.MaxPoolSize);
});
builder.Services.AddSingleton(services =>
{
    var options = new DbContextOptionsBuilder<PastoDbContext>()
        .UseNpgsql(services.GetRequiredKeyedService<NpgsqlDataSource>("RoomCleanup"),
            postgres => postgres.CommandTimeout(5))
        .Options;
    return new RoomCleanupStore(new PooledDbContextFactory<PastoDbContext>(options, poolSize: 1));
});
builder.Services.AddSingleton<IRoomCleanupStore>(services => services.GetRequiredService<RoomCleanupStore>());
builder.Services.AddSingleton<CleanUpRooms>();
builder.Services.AddOptions<HubAdmissionOptions>().BindConfiguration("Api:Hub")
    .Validate(options => options.IsValid(), "SignalR connection limits and timeout must be valid.").ValidateOnStart();
builder.Services.AddSingleton(services => new RoomConnections(services.GetRequiredService<ILogger<RoomConnections>>(),
    services.GetRequiredService<IOptions<HubAdmissionOptions>>().Value.GlobalConnections));
builder.Services.AddSingleton<RoomCleanupStatus>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<RoomCleanupWorker>();
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessCheck>("postgres",
    tags: ["ready"], timeout: TimeSpan.FromSeconds(2));
builder.Services.AddOptions<ApiOptions>().BindConfiguration("Api")
    .Validate(options => options.IsValid(), "API rate limits and trusted proxy IP addresses must be valid.").ValidateOnStart();
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ApiOptions>>((options, api) =>
{
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    options.ForwardedHeaders = api.Value.TrustedProxies.Length == 0 ? ForwardedHeaders.None
        : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var proxy in api.Value.TrustedProxies) options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});
builder.Services.AddOptions<HttpWorkOptions>().BindConfiguration("Api:HttpWork")
    .Validate(options => options.IsValid(), "HTTP work limits and overload thresholds must be valid.").ValidateOnStart();
builder.Services.AddSingleton<HttpWorkAdmission>();
builder.Services.AddSingleton<SourceRateLimits>();
builder.Services.AddSingleton<IStorageFailureClassifier, StorageFailureClassifier>();
builder.Services.AddSingleton<JoinSourceCooldown>();
builder.Services.AddSingleton<JoinPressureLimiter>();
builder.Services.AddSingleton<RoomJoinQuota>();
builder.Services.AddScoped<IRoomJoinStore>(services => new RoomJoinStore(
    services.GetRequiredService<IDbContextFactory<PastoDbContext>>(), services.GetRequiredService<RoomJoinQuota>()));
builder.Services.AddScoped<JoinRoom>();
builder.Services.AddScoped<IParticipantVerifier, ParticipantVerifier>();
builder.Services.AddSingleton<ParticipantCredentialFailures>();
builder.Services.AddScoped<IParticipantRecoveryStore, ParticipantRecoveryStore>();
builder.Services.AddScoped<GetParticipant>();
builder.Services.AddScoped<IRoomStateStore, RoomStateStore>();
builder.Services.AddScoped<GetRoomState>();
builder.Services.AddSingleton<ParticipantRateLimits>();
builder.Services.AddAuthentication(ParticipantAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ParticipantAuthenticationHandler>(
        ParticipantAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<RoomHubFailureFilter>();
builder.Services.AddSingleton<HubConnectionQuota>();
builder.Services.AddSingleton<HubNegotiateLimiter>();
builder.Services.AddSingleton<RoomHubAdmission>();
builder.Services.AddSignalR().AddHubOptions<RoomHub>(options =>
{
    options.AddFilter<RoomHubAdmission>();
    options.AddFilter<RoomHubFailureFilter>();
});
builder.Services.AddOptions<HubOptions<RoomHub>>().Configure<IOptions<HubAdmissionOptions>>((options, admission) =>
{
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(admission.Value.ClientTimeoutSeconds);
});
builder.Services.AddSingleton<RoomChangeNotifications>();
builder.Services.AddOptions<CreateRoomOptions>()
    .Bind(builder.Configuration.GetSection("RoomCreation"))
    .Validate(options => options.MaxCodeAttempts is >= 1 and <= 20, "MaxCodeAttempts must be between 1 and 20.")
    .ValidateOnStart();
builder.Services.AddSingleton<IRoomSecrets, RoomSecrets>();
builder.Services.AddScoped<IRoomCreationStore, RoomCreationStore>();
builder.Services.AddScoped<CreateRoom>();
builder.Services.AddSingleton<RoomAddQuota>();
builder.Services.AddScoped<IItemAdditionStore>(services => new ItemAdditionStore(
    services.GetRequiredService<IDbContextFactory<PastoDbContext>>(), services.GetRequiredService<RoomAddQuota>()));
builder.Services.AddScoped<AddItem>();
builder.Services.AddScoped<IItemSnapshotStore, ItemSnapshotStore>();
builder.Services.AddScoped<GetItemSnapshot>();
builder.Services.AddScoped<IItemDeletionStore, ItemDeletionStore>();
builder.Services.AddScoped<DeleteOwnItem>();
builder.Services.AddScoped<IRoomClosureStore, RoomClosureStore>();
builder.Services.AddScoped<CloseRoom>();

var app = builder.Build();
if (deployed || migrate)
{
    var factory = app.Services.GetRequiredService<IDbContextFactory<PastoDbContext>>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DeploymentDatabase));
    var prepared = await DeploymentDatabase.PrepareAsync(factory, logger, migrate, app.Lifetime.ApplicationStopping);
    if (!prepared || migrate)
    {
        await app.DisposeAsync();
        return prepared ? 0 : 1;
    }
}

app.UseMiddleware<ApiFailureMiddleware>();
app.UseStatusCodePages(status =>
{
    var response = status.HttpContext.Response;
    response.Headers.CacheControl = "no-store";
    return ApiProblem.WriteAsync(status.HttpContext, response.StatusCode,
        response.StatusCode == 413 ? "RequestBodyTooLarge" : "InvalidRequest");
});
app.UseForwardedHeaders();
app.UsePastoFrontend(deployed);
app.UseWebSockets();
app.UseRouting();
app.Use(RoomHub.CheckRequestAsync);
app.UseMiddleware<PublicJoinAdmission>();
app.Use(HttpWorkEndpoints.LimitPublicBodyAsync);
app.UseMiddleware<ParticipantCredentialAdmission>();
app.Use(SourceRateLimits.CheckDatabaseAsync);
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<HubNegotiateAdmission>();
app.MapRoomCreation();
app.MapParticipants();
app.MapRoomStateAndClosure();
app.MapItems();
app.MapPastoHealth();
app.MapHub<RoomHub>(RoomHub.Path, options => options.Transports = HttpTransportType.WebSockets).RequireAuthorization();
// 啟動時不自動 Migrate，避免單純啟動服務就修改資料庫。
await app.RunAsync();
return 0;

static NpgsqlDataSource CreateDataSource(string connectionString, int maxPoolSize, bool pooling = true)
{
    var connection = new NpgsqlConnectionStringBuilder(connectionString)
    {
        MaxPoolSize = maxPoolSize,
        MinPoolSize = 0,
        Pooling = pooling,
        IncludeErrorDetail = false,
        LogParameters = false
    };
    return NpgsqlDataSource.Create(connection.ConnectionString);
}

// WebApplicationFactory 載入實際 Program 與 DI／middleware。
public partial class Program;
