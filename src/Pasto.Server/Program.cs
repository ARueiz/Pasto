using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Npgsql;
using Pasto.Server.Api;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// 瀏覽器 SignalR 傳輸會在 query string 放憑證，停用會記錄該字串的框架請求日誌。
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// 在 JSON 綁定／字串配置前由 Kestrel 限制實際 body bytes，包含未知長度的串流。
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);

// 連線由環境變數 ConnectionStrings__Pasto 或 .NET configuration 提供；不在 repo 保存密碼。
// Compose 的 .env 只供 Compose 使用，不會自動匯入 ASP.NET Core 的設定。
var connectionString = builder.Configuration.GetConnectionString("Pasto");
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
    var connection = new NpgsqlConnectionStringBuilder(connectionString)
    {
        MaxPoolSize = settings.MaxPoolSize,
        MinPoolSize = 0,
        Pooling = true,
        IncludeErrorDetail = false,
        LogParameters = false
    };
    return NpgsqlDataSource.Create(connection.ConnectionString);
});
builder.Services.AddDbContextFactory<PastoDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>()));
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
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>()
    .Configure<IOptions<ApiOptions>>((options, api) => CreationRateLimiting.Configure(options, api.Value));
builder.Services.AddSingleton<IStorageFailureClassifier, StorageFailureClassifier>();
builder.Services.AddScoped<IRoomJoinStore, RoomJoinStore>();
builder.Services.AddScoped<JoinRoom>();
builder.Services.AddScoped<IParticipantVerifier, ParticipantVerifier>();
builder.Services.AddScoped<IParticipantRecoveryStore, ParticipantRecoveryStore>();
builder.Services.AddScoped<GetParticipant>();
builder.Services.AddScoped<IRoomStateStore, RoomStateStore>();
builder.Services.AddScoped<GetRoomState>();
builder.Services.AddSingleton<ParticipantReadLimiter>();
builder.Services.AddSingleton<ParticipantWriteLimiter>();
builder.Services.AddAuthentication(ParticipantAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ParticipantAuthenticationHandler>(
        ParticipantAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddSingleton<RoomChangeNotifications>();
builder.Services.AddOptions<CreateRoomOptions>()
    .Bind(builder.Configuration.GetSection("RoomCreation"))
    .Validate(options => options.MaxCodeAttempts is >= 1 and <= 20, "MaxCodeAttempts must be between 1 and 20.")
    .ValidateOnStart();
builder.Services.AddSingleton<IRoomSecrets, RoomSecrets>();
builder.Services.AddScoped<IRoomCreationStore, RoomCreationStore>();
builder.Services.AddScoped<CreateRoom>();
builder.Services.AddScoped<IItemAdditionStore, ItemAdditionStore>();
builder.Services.AddScoped<AddItem>();
builder.Services.AddScoped<IItemSnapshotStore, ItemSnapshotStore>();
builder.Services.AddScoped<GetItemSnapshot>();
builder.Services.AddScoped<IItemDeletionStore, ItemDeletionStore>();
builder.Services.AddScoped<DeleteOwnItem>();
builder.Services.AddScoped<IRoomClosureStore, RoomClosureStore>();
builder.Services.AddScoped<CloseRoom>();

var app = builder.Build();
app.UseMiddleware<ApiFailureMiddleware>();
app.UseStatusCodePages(status => ApiProblem.WriteAsync(status.HttpContext,
    status.HttpContext.Response.StatusCode,
    status.HttpContext.Response.StatusCode == 413 ? "RequestBodyTooLarge" : "InvalidRequest"));
app.UseForwardedHeaders();
app.UseWebSockets();
app.Use(RoomHub.CheckOriginAsync);
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRoomCreation();
app.MapParticipants();
app.MapRoomStateAndClosure();
app.MapItems();
app.MapHub<RoomHub>(RoomHub.Path).RequireAuthorization();
// 啟動時不自動 Migrate，避免單純啟動服務就修改資料庫。
app.Run();

// WebApplicationFactory 載入實際 Program 與 DI／middleware。
public partial class Program;
