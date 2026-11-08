# Pasto — PostgreSQL 整合測試與 Migration

更新：2026-11-09，S1-03。測試引用正式 `Pasto.Server`，每個 DB 案例使用獨立 schema，由正式 Migration 建表。已有 Application CreateRoom，尚無產品 API、前端或 SignalR。

## 1. 環境與相依

| 項目 | 固定版本／用途 |
| --- | --- |
| .NET SDK | 10.0.402，見 root global.json |
| PostgreSQL | 18.6，見 compose.yaml |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3，正式專案的 provider |
| EF Core／Relational／Design | 10.0.4，Design 為工具相依且 PrivateAssets=all |
| dotnet-ef | 10.0.4，本機工具，見 dotnet-tools.json |
| Microsoft.NET.Test.Sdk／xunit／runner | 18.0.1／2.9.3／3.1.5 |

兩個專案都有 `packages.lock.json`；測試透過 ProjectReference 使用正式 provider，不再維護測試用 DbContext 或 DDL。未新增 Testcontainers、SQLite、InMemory 或通用 Repository。

## 2. 啟動本機 PostgreSQL

在 repo 根目錄執行。若已有 `.env` 或資料庫，直接沿用，勿覆蓋或重建資料。

```powershell
Copy-Item .env.example .env
# 編輯 .env，填入自己的 PASTO_POSTGRES_PASSWORD。
docker compose config --quiet
docker compose up -d --wait
docker compose ps
```

主機 `127.0.0.1`、port `55432`、DB `pasto_test`、使用者 `pasto_test`。Docker CLI 找不到時，重開終端機或確認 PATH。Compose 健康檢查僅確認 server 接受連線，不代表密碼／Migration／測試已驗證。

一般停止用 `docker compose down`，named volume 保留資料。`docker compose down --volumes` 會刪除整個 DB，不當作一般停止指令。初始化後修改 `.env` 不會改既有 DB 密碼。

## 3. 建置與測試

`.env` 不會自動匯入 PowerShell 或 ASP.NET Core。請另外設定測試連線，使用與 DB 相同的密碼，不將實際連線字串存入 repo 或日誌。

```powershell
$env:PASTO_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55432;Database=pasto_test;Username=pasto_test;Password=<本機測試密碼>'
dotnet tool restore
dotnet restore Pasto.slnx --locked-mode
dotnet build Pasto.slnx --configuration Release --no-restore
dotnet test Pasto.slnx --configuration Release --no-build --no-restore
dotnet format Pasto.slnx --no-restore --verify-no-changes
```

預期 24 個通過、0 個失敗（23 個真 DB 案例、1 個憑證／房碼生成測試）。DB 案例未提供連線／DB 不可用時失敗，不 skip。測試只接受 loopback，且 DB 名稱須以 `_test` 結尾，執行者仍須確認是專用測試 DB。

| 案例 | 驗證內容 |
| --- | --- |
| CreatorIntegrityTests（3 例） | 正式 CreateRoom 成功、跨房 Creator／缺房主在 commit 被拒絕且回滾，原房保留 |
| RoomCreationTests（11 例） | 已用碼／冷卻碼碰撞、重試上限、非房碼 unique 錯誤不重試、寫入後／commit 前失敗、取消、commit 後結果遺失、兩交易房碼競爭 |
| OwnershipAndCascadeTests（5 例） | Owner 同房正反例、帶文字實際刪房 cascade／保留碼／他房保留、單刪 Creator／被引用保留碼拒絕 |
| RoomSecretsTests（1 例） | 六碼格式、128-bit 原始隨機值的 Base64URL 格式與 SHA-256 編碼一致性，不以抽樣宣稱密碼學強度 |
| Migration_PreservesCompositeForeignKeysAndCascadeDefinitions | pg_constraint 的五條 FK 欄位／參照、NO ACTION／CASCADE、deferred 定義 |
| Migration_CreatesChecksAndRequiredIndexes | 八條 CHECK、部分索引、排序／Owner／唯一索引實際定義 |
| Migration_CanReapplyAndRollbackWithoutModelDrift | snapshot 無模型漂移；重跑不重建、Down 可拆循環、Up 可再套用 |
| MigrationHistory_IsIsolatedPerTestSchema | 兩個 schema 各自建表／記 Migration；回退其中一個不影響另一個 |

Creator 案例已改跑正式 CreateRoom／RoomCreationStore，移除原本的建房寫入 helper。`TestRoomCreation` 只組裝正式服務；故障／非法資料由測試 interceptor 注入，產品沒有測試專用開關。Owner／cascade 測試的文字資料準備仍為 fixture，正式新增文字流程在 S1-04 實作。

## 4. Migration 與 host 的操作

正式模型：[PastoDbContext](../src/Pasto.Server/Infrastructure/Persistence/PastoDbContext.cs)。初始 Migration 與 snapshot 位於同層的 `Migrations/`。

工具使用獨立 design-time factory，不啟動 Web host。連線必須明確指定；單純產生 Migration／SQL script 不會連 DB，`database update` 才會變更指定 DB。不要把共用／正式 DB 當練習目標。

```powershell
$env:ConnectionStrings__Pasto = $env:PASTO_TEST_CONNECTION_STRING
dotnet ef migrations has-pending-model-changes --project src/Pasto.Server
dotnet ef migrations script --project src/Pasto.Server --output src/Pasto.Server/obj/migration.sql
# 確認是專用目標 DB 並審查 SQL 後，才手動執行：
# dotnet ef database update --project src/Pasto.Server
```

手動 update 使用連線預設 schema，與測試的 GUID schema 分開；測試不需要先跑 update。正式 host 也使用 `ConnectionStrings__Pasto`，啟動時不自動 Migrate。S1-03 仍無 endpoint，啟動後所有路徑為 404。建房設定與編碼規則見 [S1-03 導讀](s1-03-room-creation.md)。

EF 能追蹤的 CHECK／部分索引保留在 mapping 與 snapshot，初始 Migration 依 ADR 0003 以明確 SQL 建立；DEFERRABLE 不在 EF 模型內，日後任何重建這兩條 FK 的 Migration 都須手動保留並跑 catalog 測試。歷史 Migration 不引用目前 Domain 常數，避免規則變更影響舊 DDL。

相較 S1-01，EF 依 Creator 複合 FK 慣例額外建立 `IX_rooms_id_creator_participant_id (id, creator_participant_id)`。本階段刻意保留，不改變外鍵保證；因 `id` 已是 PK，這個索引的查詢效益可能有限。測試固定四張產品表的 13 個索引，加上 Migration history 的 PK，共 14 個索引名稱，後續增刪須明確審查。產生的 `obj/migration.sql` 已受既有 ignore 規則排除，只供檢閱，正式來源仍為 Migration。

工具行為參考：[EF design-time factory](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation)、[自訂 Migration](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing)。

## 5. 隔離、清理與實測界線

每例 `pasto_it_<32位GUID>`，search_path 只指向自身 schema，包含 `__EFMigrationsHistory`。先建 schema，再由 Migrator 管理交易；初始化失敗會清自己的 schema，正常結束由 await using 清理。不用 EnsureCreated／EnsureDeleted、不 DROP DATABASE、不清 public，也不掃刪其他測試遺留 schema。

若程序被強制終止可能遺留 schema。DBeaver 可連相同 DB，先確認名稱來源後再人工處理；正常完成看不到永久測試表是預期行為。

S1-02 當時實測：Windows、SDK 10.0.402、runtime 10.0.13、Docker PostgreSQL 18.6，Release 六個案例通過。之前曾被 Windows Smart App Control 封鎖 DLL（0x800711C7）；環境調整後可正常執行，不是測試斷言失敗。

### S1-02 審查後驗證紀錄（2026-11-08）

- 測試當時基準 commit：`211a2a6`；分支：`feature/s1-02-persistence`。當時結果對應未提交的工作目錄；後續實作提交為 `6846558`，並經 PR #1 合併至 `00a9890`。提交前只額外移除 DbContext 檔尾空白行，無邏輯變更；本機確認 merge tree 與 `6846558` 相同。
- `dotnet restore Pasto.slnx --locked-mode`：通過。
- `dotnet build Pasto.slnx -c Release --no-restore`：通過，0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore`：連續兩輪各 6 passed／0 failed／0 skipped，維持 xUnit 預設的跨 class 並行。
- 測試前後唯讀查詢 `^pasto_it_[0-9a-f]{32}$`：皆為 0 個 schema，沒有新增殘留。
- 原始結果保存在本機 `tests/Pasto.IntegrationTests/TestResults/s1-02-verified-run1.trx` 與 `s1-02-verified-run2.trx`；TestResults 已 ignore，不納入版本控制。
- 擴充索引集合斷言時，首次漏列 `PK___EFMigrationsHistory`，造成 1 failed／5 passed；補齊後才取得上述兩輪通過結果。首次失敗紀錄保留於同目錄 `s1-02-review-run1.trx`。

Claude 本輪僅做靜態審查；上述測試由實作方重跑，不宣稱為獨立測試驗證。S1-02 現已 commit／merge，但整個 S1 尚未完成。維護導讀與合併紀錄見 [S1-02 報告](s1-02-maintenance-report.md)。

### S1-03 驗證紀錄（2026-11-08）

- 基準 `00a9890`；分支 `feature/s1-03-create-room`，尚未提交。沿用 schema，沒有新增 Migration、套件或基礎設施。
- Release Build：0 warning／0 error；格式檢查通過。最終完整測試 23 passed／0 failed／0 skipped；測試前後均為 0 個測試 schema。
- 鎖定版本還原通過；首次在沙箱內查 NuGet 弱點資料遇到 NU1900（網路受限），取得連線權限後重跑成功，沒有關閉 audit 或修改套件版本。
- 首次 Build 遇到 xUnit2031（Assert.Single 前不應先 Where），已改用 predicate overload，沒有降低斷言。
- 首次 DB 測試 22 passed／1 failed：跨房 Creator interceptor 誤改 entity，導致新房主 ID 跟著變成既有 ID，先觸發 `PK_room_participants`。改為在同交易用參數化 SQL 只污染已寫入 Room 的 Creator 欄位，保留 FK 啟用；最終驗證為 commit 的 `fk_rooms_creator`／23503。
- 本機 TRX：`tests/Pasto.IntegrationTests/TestResults/s1-03-initial.trx`（首次失敗）、`s1-03-verified.trx`（修正後通過），皆受 ignore 規則排除。
- 提交後例外的案例會保留已 commit 的三筆資料，並確認沒有自動重試；這個結果不應被說成 rollback 失敗。

### S1-03 審查後補驗證（2026-11-09）

- Claude 靜態審查無 P0～P2；核對既有 TRX，未獨立執行測試。
- F1 保留 commit 的 CancellationToken，在程式註解與導讀明確記錄結果不確定語意；新增已提交後收到取消例外的測試，不宣稱模擬真實網路競爭。
- Release Build（0 warning／0 error）、格式檢查通過；完整測試 24 passed／0 failed／0 skipped；測試前後均為 0 個測試 schema。
- TRX：`tests/Pasto.IntegrationTests/TestResults/s1-03-review-20261109.trx`，保留先前 23 例的紀錄。
- F2 尚無 SQL 往返／效能量測，保留預設 savepoint，延至 S8 評估。Program.cs 的 DI／ValidateOnStart 與 HTTP 取消處理測試列入 S2。

尚未驗證：新增／刪除／關房的正式交易、文字容量競爭與計數一致性、期限邊界、REST／UI／SignalR、授權與完整安全／負載測試。S1-03 的房碼碰撞競爭不等於 S1-04 的文字併發已完成。

S1-01 的歷史教學見 [入門導讀](s1-01-dotnet-postgresql-guide.md)，其中舊 DDL／mapping 不是現行實作。
