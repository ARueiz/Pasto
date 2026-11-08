# Pasto — S1-01 PostgreSQL 整合測試

日期：2026-10-20。S1-01 只建立測試骨架及兩個 Creator 正反例；沒有產品 API、前端、正式 Migration 或完整 CreateRoom use case。

第一次接觸 .NET／PostgreSQL／EF Core，請先讀 [S1-01 入門導讀](s1-01-dotnet-postgresql-guide.md)：包含建立指令、專案結構、完整欄位用途、EF／交易流程與後續 API 驗證方式。本文件保留為快速操作與實測紀錄。

## 1. 已建立的範圍

- 一個 .NET 10 測試專案：[Pasto.IntegrationTests.csproj](../tests/Pasto.IntegrationTests/Pasto.IntegrationTests.csproj)。
- 四表測試 DDL：[schema.sql](../tests/Pasto.IntegrationTests/Persistence/schema.sql)。
- 四表／關聯的 EF mapping：[RoomTestDbContext.cs](../tests/Pasto.IntegrationTests/Persistence/RoomTestDbContext.cs)。
- 每個案例的獨立 schema 與清理：[PostgresTestDatabase.cs](../tests/Pasto.IntegrationTests/PostgresTestDatabase.cs)。
- Creator 正反例：[CreatorIntegrityTests.cs](../tests/Pasto.IntegrationTests/CreatorIntegrityTests.cs)。

測試內的 Record／mapping 與 fixture 插入方法用於驗證候選模型，不是正式 Domain／Application。FK 的 DEFERRABLE／CHECK／部分索引以測試 SQL 建立，不宣稱目前 EF mapping 可直接產出完全相同的 Migration。S1-02 再擴充驗證與決定可重用的正式寫入方式。

## 2. 環境與固定版本

| 項目 | 本次版本 |
| --- | --- |
| .NET SDK | 10.0.402，root global.json 允許同 feature band 的更新 patch |
| PostgreSQL | 18.6，本次從真 DB 取得的 server version |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3，直接相依 |
| EF Core／Relational | 10.0.4，由 provider 相依並固定於 packages.lock.json |
| Npgsql | 10.0.3，由 provider 相依並固定於 lock file |
| Microsoft.NET.Test.Sdk | 18.0.1 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |

provider 10.0.3 使用 EF Core 10 系列；套件版本依 NuGet 相依資訊選定，整體組合已實際編譯與測試。[Provider 套件](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3)、[Test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.0.1)、[xUnit](https://www.nuget.org/packages/xunit/2.9.3)、[Runner](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5)

xUnit 負責案例與斷言，VSTest runner 提供 dotnet test／IDE 整合；沒有加入 Testcontainers、EF Design、InMemory、SQLite 或額外斷言套件。直接及間接相依均以 packages.lock.json 鎖定，依賴更新應明確 Review。

## 3. 資料庫準備

可以沿用既有的本機專用 PostgreSQL，或使用根目錄的 [compose.yaml](../compose.yaml)。Compose 僅建立測試資料庫，不建立產品資料表；目前測試仍自行管理獨立 schema。

首次使用 Compose（在專案根目錄執行）：

```powershell
Copy-Item .env.example .env
# 編輯 .env，將 PASTO_POSTGRES_PASSWORD 填入自己的本機測試密碼。
docker compose config --quiet
docker compose up -d --wait
docker compose ps
```

若已有 `.env`，請直接編輯，不要覆蓋。資料庫為 `127.0.0.1:55432`／`pasto_test`，使用者為 `pasto_test`，密碼使用 `.env` 所填值。啟動前確認 55432 沒有被既有 DB 占用。若終端機找不到 `docker`，請重開終端機；仍找不到時確認 Docker CLI 安裝路徑是否已加入 PATH。

一般停止用 `docker compose down`，named volume 仍保留資料。`docker compose down --volumes` 會刪除整個測試資料庫，不用於一般停止。官方映像只在空資料目錄初始化帳密，修改 `.env` 不會變更既有資料庫密碼。Compose 健康檢查僅確認 server 可接受連線，不代表整合測試已通過。

以下保留先前 `docker run` 的替代方式；請與 Compose 擇一，不要同時占用相同連接埠：

```powershell
docker run --name pasto-postgres-test --rm -e POSTGRES_USER=pasto_test -e POSTGRES_PASSWORD=pasto_test_local -e POSTGRES_DB=pasto_test -p 127.0.0.1:55432:5432 -d postgres:18.6
docker exec pasto-postgres-test pg_isready -U pasto_test -d pasto_test
```

此密碼只是前面本機示範環境的測試值；改過設定時請使用自己的測試資料庫連線資訊。這個 --rm 容器是可拋棄資料環境，停止後匿名資料 volume 隨容器移除，不用於正式資料。[PostgreSQL image](https://hub.docker.com/_/postgres)

測試角色需能在專用 DB 建立／刪除 schema 及表。測試會拒絕非 loopback Host，且 Database 必須以 `_test` 結尾；只接受 `127.0.0.1`、`localhost` 或 `::1`。這是避免誤連的本機保護，執行者仍須確認它是專用測試 DB。

## 4. 執行方式（PowerShell，專案根目錄）

連線只從程序環境變數讀取，不寫入 repo 或一般測試輸出。Compose 的 `.env` 不會自動設定 PowerShell 的測試連線變數；請將下列密碼替換為實際值（使用 Compose 時與 `.env` 一致）：

```powershell
$env:PASTO_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55432;Database=pasto_test;Username=pasto_test;Password=<本機測試密碼>'
dotnet restore tests/Pasto.IntegrationTests/Pasto.IntegrationTests.csproj --locked-mode
dotnet test tests/Pasto.IntegrationTests/Pasto.IntegrationTests.csproj --configuration Release --no-restore --logger 'console;verbosity=normal'
```

預期為 2 個通過、0 個失敗，指令 exit code 為 0。未設定連線、DB 未啟動、帳密錯誤或 SQL／斷言錯誤會失敗，不會 skip 或改用記憶體 DB。正常日誌不輸出連線字串；若需要 server version／schema 名稱，可將 logger 改成 `console;verbosity=detailed`。

程式格式檢查：

```powershell
dotnet format tests/Pasto.IntegrationTests/Pasto.IntegrationTests.csproj --no-restore --verify-no-changes
```

沒有 root solution，請使用上面的專案路徑；本階段沒有前端 Lint 或產品 Build 指令。

## 5. 兩個案例如何驗證

| 案例 | 操作 | 成功條件 |
| --- | --- | --- |
| SameRoomCreator_CommitsThroughStagedEfWrites | 同一 transaction：先 EF 寫保留碼，再寫 Room，最後寫同房 Participant，各步 SaveChanges | commit 成功；新 DbContext 可讀回 Room／Creator／保留碼且關聯正確，5 小時／24 小時 fixture 時間一致 |
| CreatorFromAnotherRoom_IsRejectedAtCommitAndRollsBackNewRoom | 先成功建 A 房及其房主；B 房的 Creator 指向 A 的房主，B 仍建立自己的 Participant；逐步 SaveChanges 後嘗試 commit | DB 回 SQLSTATE 23503、ConstraintName=fk_rooms_creator；B Room／Participant／保留碼全回滾，A 房及房主仍存在 |

反例的 Participant 真實存在，所以測到的是「屬於不同房間」的拒絕，不只是不存在的 ID。SaveChanges 先成功、commit 才拒絕，也驗證到延後 FK 檢查的路徑。這次只證明該分階段 fixture 可行，沒有證明把整個循環 graph 一次 SaveChanges 就能成功。

缺少連線的失敗行為也已手動反向確認：清掉 PASTO_TEST_CONNECTION_STRING 後執行正例，dotnet test 回 exit code 1，錯誤為明確的設定提示。這是刻意驗證防呆，不是被忽略的正常測試失敗。

## 6. Schema 隔離與 DBeaver

每個案例產生 `pasto_it_<32位GUID>`，connection search_path 只指該 schema；DDL、EF 寫入與驗證皆使用它。不同案例可獨立執行，不共用測試資料。schema 建立與 DDL 在同交易，初始化失敗就回滾。

案例結束由 await using 清除自己產生的 schema；不呼叫 EnsureCreated／EnsureDeleted、不清 public、不 DROP DATABASE。資料保護不依賴其他人的表名。請勿在自動化案例執行中手動改該 schema 的資料。

DBeaver 仍可連 `127.0.0.1:55432`／pasto_test，執行：

```sql
SELECT schema_name
FROM information_schema.schemata
WHERE schema_name ~ '^pasto_it_[0-9a-f]{32}$'
ORDER BY schema_name;
```

正常完成後應沒有這次留下的 schema，所以看不到永久測試表是預期結果。若要學習觀察表／約束，可 debug 單一案例，在 CreateAsync 完成後設中斷點，再於 DBeaver refresh Schemas；繼續執行後會清掉。程序被強制終止可能遺留 schema，確認確為中止測試產生的名稱後再人工清理，不自動掃刪其他 schema。

## 7. 本次實測紀錄與界線

- 環境：.NET SDK 10.0.402、測試 runtime 10.0.13、PostgreSQL 18.6，沿用已啟動的本機 DB。
- Debug 整合測試：2／2 通過。
- Release 整合測試：2／2 通過（含 constraint mapping 名稱更新後重跑）。
- dotnet format --verify-no-changes：通過。
- 缺少連線的控制實驗：dotnet test 如預期回 exit code 1。

本次執行環境找不到 Docker CLI，沒有另外建立／重啟容器；整合測試直接連既有的 PostgreSQL。套件還原初次因使用者 .dotnet 快取權限失敗，取得執行權限後已成功還原。

尚未驗證：Owner 反例、cascade 正反例、完整失敗注入、一次 tracked graph 寫入、容量／並行／鎖、到期邊界、效能／1,000 連線、REST／UI／SignalR。這些屬 S1-02 以後，不能由本次兩個案例推論已通過。
