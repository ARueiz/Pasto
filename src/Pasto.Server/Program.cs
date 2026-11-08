using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// 連線由環境變數 ConnectionStrings__Pasto 或 .NET configuration 提供；不在 repo 保存密碼。
// Compose 的 .env 只供 Compose 使用，不會自動匯入 ASP.NET Core 的設定。
var connectionString = builder.Configuration.GetConnectionString("Pasto");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings__Pasto before starting Pasto.Server.");
}

// Factory 讓每次建房嘗試擁有自己的 context；也會註冊 scoped PastoDbContext 供後續 use case 使用。
builder.Services.AddDbContextFactory<PastoDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddOptions<CreateRoomOptions>()
    .Bind(builder.Configuration.GetSection("RoomCreation"))
    .Validate(options => options.MaxCodeAttempts is >= 1 and <= 20, "MaxCodeAttempts must be between 1 and 20.")
    .ValidateOnStart();
builder.Services.AddSingleton<IRoomSecrets, RoomSecrets>();
builder.Services.AddScoped<IRoomCreationStore, RoomCreationStore>();
builder.Services.AddScoped<CreateRoom>();

var app = builder.Build();
// S1-03 已可由 Application 建房，尚無產品 endpoint；所有路徑目前回 404。
// 啟動時不自動 Migrate，避免單純啟動服務就修改資料庫。
app.Run();
