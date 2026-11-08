using Microsoft.EntityFrameworkCore;
using Pasto.Server.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// 連線由環境變數 ConnectionStrings__Pasto 或 .NET configuration 提供；不在 repo 保存密碼。
// Compose 的 .env 只供 Compose 使用，不會自動匯入 ASP.NET Core 的設定。
var connectionString = builder.Configuration.GetConnectionString("Pasto");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings__Pasto before starting Pasto.Server.");
}

builder.Services.AddDbContext<PastoDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();
// S1-02 僅提供 host／持久層骨架，尚無產品 endpoint；所有路徑目前回 404。
// 啟動時不自動 Migrate，避免單純啟動服務就修改資料庫。
app.Run();
