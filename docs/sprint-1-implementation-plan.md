# Pasto — Sprint 1 分階段實作計畫

日期：2026-10-20；2026-11-05 依 [ADR 0003](decisions/0003-mvp-scope-reduction.md) 調整 S1-02 之後的內容。執行狀態：**S1 已合併；S2 已經 PR #5 合併（9eace5f）；S3-01 文字寫入／配額已實作，待獨立 review；S3-02 快照列表尚未開始**（2026-11-18）。已有建房／加入／Bearer／me API，連續兩輪完整 269 例測試通過。S2 決策見 [S2 工作項](s2-api-identity.md)，實測見 [testing.md](testing.md)。讀取 quota 依使用者選擇維持，風險仍在並列入 S7；S2 已合併。

依據：[最終需求](requirements.md)、[最終架構](architecture.md)、[ADR 0002](decisions/0002-room-code-and-anonymous-participants.md)、[ADR 0003](decisions/0003-mvp-scope-reduction.md)。ADR 0001 僅供歷史比較，不是實作來源。

## 1. 執行原則與順序

以小範圍、可驗證的變更逐步完成 MVP。每階段可分成數個獨立 Review 的小 patch；先執行與本次變更相關的 Build／Lint／Tests，記錄實際結果，不在全部完成前稱 MVP 已完成。

既有交付順序為 S1 持久層 → S2 匿名授權 → S3 文字／快照 → S4 基本 UI → S5 即時同步。後續順序為 **S6 → Logging → Health Checks → OpenTelemetry → S7 → Deployment → S8 → Seq／AWS**。

OpenTelemetry 先提供 S8 所需的指標。S7 先處理讀取 admission 耗盡、反覆 Join 耗盡名額等風險。Deployment 之後再以近正式的容器環境壓測。公開部署的判定及部署取捨見 [ADR 0004](decisions/0004-observability-and-deployment.md)。

S1 是資料庫設計的驗證關卡，不能跳過後直接生成完整應用。S2 開始已有基本輸入、Credential 脫敏與 ADR 0003 的基本限流；S7 補齊其餘分層防護並做完整安全／資源驗證。S7 完成前可以在只有可信任裝置與使用者能存取的內網使用，並保留驗證、限流與敏感資料保護。只要有不受信任的來源能連線，就視為公開部署，S7 完成前禁止。

S1-01 依核准建立最小必要測試專案與相依，沿用既有 DB；不預先建立大量空專案。後續若需要改已核定不變條件或增加 MVP 外需求，先提出影響與修訂，不自行擴張。

## 2. S1：正式持久層與交易整合測試

目的：用真 DB 證據排除循環 FK、EF 寫入順序、清理與併發風險，先證明不變條件再做 HTTP／UI。依 ADR 0003，S1-02 起整合測試改跑正式 Migration 與正式 use case，不再擴充測試專用的 mapping 或寫入 helper。

| 小工作 | 交付 | 驗收條件 |
| --- | --- | --- |
| S1-01 最小完整性測試骨架（已完成） | 最小 .NET 10 測試／持久層範圍、固定 PostgreSQL／EF provider 版本、可拋棄測試 DB／schema、四表測試 DDL／mapping、執行說明 | 真 PostgreSQL 可重跑；不是 InMemory／SQLite；不連共享／正式 DB；測試失敗確實使指令失敗 |
| S1-02 正式持久層骨架（已完成） | `src/Pasto.Server`（Api／Application／Domain／Infrastructure 資料夾）、root `.slnx`、本機 PostgreSQL `compose.yaml`、正式 DbContext 與初始 Migration（DEFERRABLE 複合 FK、CHECK、部分索引以 `migrationBuilder.Sql()` 加入）；測試專案改引用正式專案 | 既有兩個 Creator 案例改跑 Migration 建出的 schema 並通過；整合測試檢查 `pg_constraint` 的 deferred／cascade 定義與 S1-01 相同；移除測試用 `schema.sql` 與 `RoomTestDbContext` |
| S1-03 建房與 Owner／cascade（已經 PR #2 合併） | 正式 CreateRoom 交易（保留碼 → Room → 房主，分階段 EF＋同一 DbTransaction、Code 碰撞有界重試）與 Owner／cascade 測試 | 同房有效建立 commit；跨房 Owner／Creator、缺房主及中途失敗均不留下部分資料；刪 Room 清 Participant／items，保留碼仍在 |
| S1-04 併發交易核心 | 正式的新增／刪除／關房交易路徑與整合測試，分成新增、刪除、關房三個小 patch | 299 筆併發新增僅一個成功；刪同筆只扣一次；Owner 權限正確；關房／新增／刪除同鎖序，無負計數／額外 Version，TotalBytes 等於實際統計且不作容量拒絕 |
| S1-05 期限邊界 | 到期邊界、等待鎖後到期、DeletedAt 的測試 | 過期／DeletedAt 在鎖後依 DB 當下時間重驗；原子 rollback、FK deferred commit／違約失敗均符合預期。保留碼回收的測試隨 S6 Worker 一起做 |

S1-01 已交付；之後每個小工作都直接擴充可重用的正式程式，不在測試內另寫一套永遠不會被應用使用的商業實作。

測試設計要求：

- 至少兩個獨立 DbContext／DB 連線交錯交易，以 barrier／明確同步點建立競爭；不只測循序呼叫，也不靠任意長 sleep 猜時序。
- 注入在 Room 插入後、Participant 插入後、commit 前失敗，檢查 Room／Participant／Code reservation 都回滾；跨房錯誤不得被只在測試 fixture 中關閉的 FK 掩蓋。
- EF tracked graph 可能先判循環；測實際選定寫入路徑、tracking 狀態及 DbTransaction，共用 transactional connection，不只測 DB 支援 DEFERRABLE。
- 測到期前／檢查點等於或晚於 ExpiresAt／已關房，以及持鎖等待後再檢查。以可控制 fixture 時間與 DB 當下時間驗證，避免只用 Client 時鐘或交易起始時間。
- 200 筆×8 KiB 超過舊 1.5 MiB 必須可成立，300／301 筆與單筆 byte 邊界分別驗證；沒有每房總量上限的測試設定。
- 每個案例仍使用獨立 schema；S1-02 起以正式 Migration 建立該 schema，而非測試用 DDL。Migration 內手寫的約束 SQL 需 Review，並由測試檢查實際約束定義。

S1-01 已採 EF Core／PostgreSQL provider、xUnit 與 VSTest runner，固定版本及直接／間接相依 lock file 見 [testing.md](testing.md)。沒有加入 Testcontainers；S1-02 已新增產品專案、Migration 工具鏈與 Compose，相依用途與版本見 testing.md。

**S1 出口條件**：上述不變條件有真 DB 的通過／失敗證據，記錄採用 FK／EF 寫入方式與指令。若循環成本過高，先提出同樣保證同房／必有房主／原子性／無孤兒的替代，不以 IsCreator 唯一索引或應用檢查單獨降級 DB 保證；未解決前不展開依賴此模型的大量 API。

## 3. S2：房間、匿名 Participant 與授權 REST

交付以小 use case 拆分：建房與房主 Credential → Join／恢復 → 房間狀態與房主關房。在 S1-02 建立的 `Pasto.Server` 上加入 REST endpoint、DI／設定與 ProblemDetails；schema 沿用 S1 的正式 Migration，不含檔案表。

同時具備 CSPRNG 六碼／唯一碰撞有界重試、Credential hash、sessionStorage 對應契約、5 小時／DeletedAt 驗證；ADR 0003 的基本限流（建房／加入每 IP＋全站）、可信代理設定、no-store／日誌脫敏與有限 DB pool 先到位。沿用 S1 交易核心，不另寫第二套邏輯。

驗收：

- AC-01～AC-03 的建房／加入／跨房權限部分、AC-10 的即時失效，以及 AC-15 的不確定 POST 錯誤語意可用 HTTP 整合測試驗證。
- 建房回應／QR 用的 URL 不帶 Credential；/me 恢復不新增身分；無效 Credential 不取得他房身分。
- 一般人不能關房，房主關房後讀寫／加入拒絕；DB／通知暫時未接完整時不顯示假成功。
- 建房／加入超過限流回 429 與 Retry-After；不觸碰使用者既有或正式資料庫。

S2 驗證另須直接覆蓋 Program.cs 的 DI 組裝、生命週期、預設／非法 MaxCodeAttempts 與 ValidateOnStart，以及 commit 期間取消的結果不確定語意；不得只複製註冊程式到測試。

使用者已核准測試相依與 metadata API 歸 S2，開工前五項決策見 [S2 工作項](s2-api-identity.md)。S2-01 已交付建房與必要防護，Claude 靜態審查無 P0～P2，日誌 P3 修正保留於 `58e7926`。S2-02 已交付加入、共用憑證驗證器、AuthenticationHandler 自訂 401 body、me、查憑證前 IP／全站及驗證後 Participant 讀取配額；S2-03 已交 metadata／關房，完整 228 例通過。各 patch 獨立 review，S2 後續由使用者確認測試審查 OK 並合併；S3 計畫見 [S3 工作項](s3-items.md)。

## 4. S3：文字 CRUD、容量及整份快照列表

交付新增／刪自己的文字與整份快照列表（ADR 0003），沿用 S2 的 metadata API。新增 8 KiB 與 JSON Body 32 KiB 分別驗證、300 筆鎖內檢查、TotalBytes 只統計；已驗證新增／刪除限流；API 不接受 Owner／Creator 欄位。不加分頁、游標或單筆同步 API。

驗收：

- AC-03～AC-07：UTF-8 多位元／空白／換行保留，8,192／8,193 bytes、32 KiB body 邊界、超舊總量可新增、第 301 筆拒絕，併發計數／Version 正確。原文合法但 JSON 過大時明確 413。
- 列表一次回傳全部有效文字，排序穩定；items、itemCount、snapshotVersion 同一交易；滿房 300 筆×8 KiB 完整回傳不截斷，記錄回應大小；查詢計畫使用房間／排序索引。
- Owner 授權在後端，房主也不能刪他人單筆；同房不存在為 changed=false，不另扣計數或加 Version。

## 5. S4：最小前端與身分／分享操作

先前由其他工具產生的 UI Prototype 已移除；開始 S4 前先與使用者討論版面與互動方向，再建立前端專案。

交付 React + TypeScript + Vite 基礎頁面：建房、輸入碼／URL 加入、Client QR、sessionStorage 恢復與主動離開、文字輸入／byte 提示／列表／複製／刪自己／房主關房。分成「身分分享」及「文字操作」兩個小 patch；尚未完成 SignalR 前以明確手動刷新驗證，不宣稱已即時。

驗收：

- AC-02／AC-03／AC-04／AC-11：手機 QR 新身分、F5 原身分、離開清本分頁、複製原文、8 KiB byte 提示、同房所有權 UI，後端仍是授權依據。
- QR 只在 Client 編碼加入 URL，不依賴外部產碼服務；按需求列最小 QR 依賴／替代方案，不加檔案功能。
- Clipboard API 拒絕有手動選取方式；HTML 只顯示文字，無 dangerouslySetInnerHTML；sessionStorage 失效／無法保存時有明確身分狀態，不謊稱可恢復。
- 前端 Build／TypeScript／Lint 與必要 UI 測試實際執行；說明分頁複製／Session Restore 可能保留副本。

## 6. S5：SignalR 與方案 A 同步

交付 Server RoomChanged／授權訂閱，再交付 Client 合併提示、單一 round、整份快照替換、generation／AbortController、重連與前景／15～20 秒 jitter 檢查。分成「授權與提交後提示」及「Client 同步」小 patch，不建立事件缺號緩衝、單筆 GET、局部刪除狀態機、Outbox 或重放框架。

驗收：

- AC-08／AC-09：先訂閱再讀；連續大量重複／亂序通知只安排有界工作；提示版本非連續也正常重建，Server 不假裝保證交付。
- 兩瀏覽器新增／刪除後 REST 收斂，通知只含 Room.Id／version；其他房無資料／提示。commit 失敗無提示，commit 後通知失敗資料仍可讀。
- 慢 GET、切房、離開、401、關房後晚回應不能覆蓋新狀態，舊房晚到的 401／404 也不能清新房；快照版本不低於已顯示版本才替換；失敗有限重試、無忙迴圈，持續寫入時明確同步中、安靜後收斂。
- 重連重驗／重新訂閱；回前景檢查。
- 故意丟最後一次通知與模擬通知前程序中斷，記錄健康前景修復延遲；先做功能證據，最終 1,000 人時間目標在 S8 驗證。

## 7. S6：Worker、冷卻回收與生命週期

交付每分鐘清理、每批最多 100 房、同鎖序與 SKIP LOCKED、每輪時間預算、不重疊／可補清；RoomCodeReservations 獨立回收；有界到期／關房連線清理。拆成「主資料」及「保留碼／連線」兩小 patch。

第一個工作項目（主資料）：

- Worker 使用獨立 Npgsql DataSource 與 DbContextFactory，連線池預設最多 4 條，`Cleanup:MaxPoolSize` 可設為 1～4。API 與 Worker 各自取得連線。
- 到期與關房各用現有部分索引，依失效時間和 UUID 排序。每類在單表查詢內用 `FOR UPDATE SKIP LOCKED` 選至本次批次上限，合併後只刪最早的一批房間。這可能暫時多鎖最多 100 房，提交或回滾時即釋放。鎖後以 DB 時間複驗，由既有 cascade 清除主資料。
- 啟動立即補清，之後每分鐘執行。同一輪共用 20 秒取消期限，每批交易另設 PostgreSQL `lock_timeout` 2 秒、`statement_timeout` 5 秒。每輪開始量測待清量及最久失效時間，量測發生資料庫錯誤時仍繼續清理。每批記錄提交數與耗時。
- 刪除或提交遇到 SQLSTATE 57014 或 Npgsql 指令逾時時，批次減半並跨輪保留。成功提交滿批後加 10，最高 100。刪除不足本次批次大小即結束該輪。批次為 1 仍逾時時記錄失敗並等下一輪，最舊的單房若持續逾時仍可能阻礙後續清理。
- Worker 記錄連續失敗輪數，完整成功一輪後歸零。未預期的輪次例外記錄固定 EventId、例外型別及堆疊框架，下一輪照常執行。啟動設定驗證仍可使 Host 啟動失敗。
- 20 秒預算用完時，若本輪已成功提交且刪除至少一房，視為正常追趕，連續失敗輪數歸零，記錄 Warning `6006 RoomCleanupRoundIncomplete`，含 `CommittedBatches` 與 `DeletedRooms`。本輪尚未提交任何房間刪除則維持 `6002` 並累加失敗輪數。資料庫錯誤、單房仍逾時及未預期例外即使已有進度也照常計為失敗。
- 正式主機預設啟用 Worker。整合測試主機預設設 `Cleanup:Enabled=false`，清理測試可直接呼叫批次執行，並以顯式啟用的主機驗證啟動補清。

第二個工作項目（保留碼與連線）：

- 保留碼沿用 `ix_code_reuse`，每批最多 100 筆，依 `ReusableAfter`、房碼順序以 `FOR UPDATE SKIP LOCKED` 選取。READ COMMITTED 交易內取得鎖後，再確認 DB 時間已達期限及無 Room 引用，提交後才算回收。現有 FK 與唯一鍵保護並行建房及回收。
- 1,500 條上限只限制單實例的訂閱追蹤表。追蹤於加入 group 前登記，失敗或斷線即移除。滿載時仍加入 group 並執行加入後的 DB 複驗，既有未追蹤連線保持原狀，新訂閱可使用釋出的名額。重複訂閱不重複占用或計數。S7 的完整 transport、IP、房間與 Participant 配額另行交付。
- 關房提交後先盡力送既有 `RoomChanged`，再由 Host 中止同房已追蹤連線。Worker 以 DB 時間每批最多複驗 100 個追蹤中的 RoomId，補清到期、已關與已刪除的房間，下一輪從上次完成的位置接續。Worker 的連線複驗與房碼回收各有 5 秒取消期限，與主資料 20 秒預算及其連續失敗計數獨立。
- 未追蹤連線由既有 `RoomChanged`、REST 404／401 及 Client 失效流程退出。Worker 每輪只在累計值為正時記錄一次 `6009 RoomConnectionTrackingFull` Warning，帶 `UntrackedSubscriptions` 並取走累計值。欄位不含連線識別與敏感資料。

驗收：

- AC-10～AC-12：Worker 停止不恢復過期存取；主資料 cascade 全清、保留碼仍在；重跑安全、停機恢復可補清。
- 提前關房不縮短原 ExpiresAt + 24 小時；回收需無 Room 引用；碰撞／回收競爭不重配有效碼；舊 Credential 無新 UUID 權限。
- 造超過 100 房的 backlog，驗證分批、公平選失效時間及每輪時間預算；量測健康情況失效後 10 分鐘目標，超時有 backlog／最久失效指標。
- 到期／關房 group 清除可能遺漏時，REST 與後續 Hub 驗證仍拒絕，Client 校正清 UI；不保留歷史查詢／還原。

## 8. Logging

- 開工前提出套件方案。直接新增 `Serilog.AspNetCore`，業務層維持 `ILogger<T>`。Console 開發輸出可讀文字，正式輸出 CLEF 或同等 JSON，包含 UTC 時間、Level、EventId、Message、TraceId、SpanId、ServiceName、Environment、ApplicationVersion。
- 正式預設 Information 且可設定，過濾 ASP.NET Core／EF Core。用 `MinimumLevel.Override` 重建目前 Hosting Diagnostics 的 Warning 門檻，停用 `UseSerilogRequestLogging()`，避免一般成功請求、心跳與廣播產生 Information。
- 保留 `ApiFailureMiddleware`。Hub Filter 的未預期例外記固定 EventId、例外型別及堆疊框架。例外物件、Message、方法參數與完整 HTTP Body 排除在日誌外，預期業務錯誤與已知 `HubException` 維持低於 Error。
- 驗收需先證明測試 provider 實際收到事件，再驗證憑證、房碼與文字均未出現。用 `writeToProviders: true` 或等效方式保留既有 `RoomApiTests`、`RoomCleanupTests`，並驗證 Console 與 provider 無重複事件。測試涵蓋 `/r/{code}`、Hub query 與失敗路徑。

## 9. Health Checks

- `/health/live` 僅檢查程序。 `/health/ready` 以既有 API `NpgsqlDataSource` 執行有界的低成本查詢，如 `SELECT 1`。使用 ASP.NET Core 內建功能。
- 另提清理 Worker 狀態的呈現方式，回報最後成功時間及連續失敗輪數。Worker 故障與追趕進度均不改變 live／ready 結果。端點略過現有限流與驗證，回應不含敏感資料或內部細節，成功探針不寫 Information。部署時只在內部開放。
- 驗收涵蓋 DB 不可達時 live 正常、ready 失敗，Worker 失敗時兩種探針結果依原條件判定。
- 外部流量無法存取 `/health/*` 的驗收列在 [Deployment](#12-deployment) 的可信代理項目。

## 10. OpenTelemetry

- 開工前提 Hosting 擴充、ASP.NET Core instrumentation 與 OTLP exporter 方案。有 OTLP 端點才註冊 exporter，無設定時仍正常運作。日誌維持 Serilog。
- HTTP 數量、錯誤率、耗時與 SignalR 連線優先訂閱 .NET 內建 meter。用 `System.Diagnostics.Metrics` 增加清理失敗及 Item 新增失敗計數。RoomId、ItemId、TraceId、ParticipantId、ConnectionId 與房碼禁止用作 metric tag。
- Tracing 從 ASP.NET Core 起步，採 W3C Trace Context，正式環境採樣可調。EF Core／Npgsql tracing 另確認需求與重複 span。文件說明 negotiate、WebSocket handshake 與 Hub 方法各自的 trace 範圍，依所選版本驗證。
- 驗收檢查實際輸出的 `url.path`、`url.query`、`url.full` 等屬性及 SignalR 相關 span，確保無 `access_token` 與實際房碼。核對套件版本的 query 遮蔽，並驗證明確的匯出前清理；metrics 的 `http.route` 使用路由模板。本機驗收須把指標和 trace 實際送到獨立容器的 .NET Aspire Dashboard 並查看，核對 Dashboard 實際接收的 trace 與日誌符合跨階段遮蔽要求，沒有房碼、`access_token`、Credential 或使用者文字。用 `dotnet-counters` 驗證可即時讀取執行中程序的內建 meter 和自訂 meter。日誌收集依既有選型，產品仍由 Serilog 輸出。
- `dotnet-counters` 與 Aspire Dashboard 只用於開發及 S8 隔離測試。Dashboard 使用固定版本的官方映像，發布的連接埠只綁 `127.0.0.1`，保留內建存取驗證，資料只在記憶體，不需持久化。標準 `deploy/` 啟動不包含 Dashboard。

## 11. S7：安全、資源與監控整合

在 S2／S3 的基本限流之上，依壓測／安全測試補齊 ADR 0003 延後的項目：公開錯碼退避／全站壓力模式、Hub 方法與 SignalR 連線配額、分類 HTTP 並行、部署資源 admission、bounded partition 與恢復遲滯；並校正需求第 5 節的候選值。敏感資訊脫敏／CSP／Origin／代理驗證及指標分開 Review。

驗收：

- AC-13／AC-15：多來源猜碼、連續錯碼、NAT 合法使用者、偽造 X-Forwarded-For、無效 Credential 及直接 Hub invocation 有預期防護，429／Retry-After、503 明確。
- S2-02 使用者已選擇維持讀取配額：單一 IP 可用掉全站 50%，少數來源可耗盡驗證前讀取配額，影響合法 me／metadata／快照同步。S3 寫入 API 與 DELETE room 也共用此 admission，因此耗盡時新增文字、刪除文字與關房同樣會被擋下，不再只影響讀取。這是仍未緩解的可用性風險；公開部署前必須處理並驗證合法重連／讀取／寫入預算、失敗壓力模式與全站／NAT 取捨，再由 S8 提供負載證據。
- 分享連結持有者可反覆 Join 耗盡 1000 個生命週期名額。公開部署前需做濫用測試、提出防護與資料預算取捨，不將 Participant 上限當成防濫用保證，也不能刪被文字引用的 Participant 來繞過預算。
- partition／失敗記錄達上限仍有界，新鍵不直接放行；通知／Client dirty／伺服器連線追蹤不因輸入數量無界成長。
- 高負載先限制建／加入，讀、刪、清理仍有預算；全站連線足夠容納目標千人，同 IP 不被過小連線額度卡住；DB pool 合計與 max_connections 有餘裕。
- 不引入全站 DB 配額列，不用 Room.TotalBytes 拒絕總量；監控資源與查 SQL／鎖等待，確認每次寫入不爭用額外全站列。
- 日誌、代理／APM／SignalR access_token 全鏈路檢查無正文／明文 Credential；CORS 不替代 WebSocket Origin／同房授權。

## 12. Deployment

S7 完成後交付。代理與瀏覽器驗收可先使用隔離容器環境。新增元件及公開端點依 AGENTS.md 先提方案。

| 工作 | 交付與驗收 |
| --- | --- |
| 同源靜態檔 | ASP.NET Core 提供 Vite 建置檔，開發維持 Vite。`ApiFailureMiddleware` 的 `no-store` 限定 API、Hub 等敏感回應。`index.html` 使用 `no-cache`，帶內容 hash 的資產長期快取。驗證刷新 `/r/{code}`、SPA fallback 排除 `/api`、`/hubs`、`/health` 及遺失靜態檔；測試 CSP、`Referrer-Policy: no-referrer`、HSTS、`X-Content-Type-Options: nosniff` 及 WSS。 |
| Caddy／TLS | 內網用 Internal CA 或自備憑證，AWS 用公開信任憑證，localhost 開發可用 HTTP。初始化設定主機名稱／IP 與 SAN，持久化 CA 及 Caddy 資料。提供電腦、iOS、Android 的根憑證信任步驟，實機驗證裝置確實信任憑證且 Clipboard API 可用。所有說明文件與初始化流程不得要求使用者忽略憑證錯誤或略過警告。Caddy 與應用程式解耦。驗收 access log 及錯誤路徑已遮蔽 URL 房碼、`access_token` 等敏感資料。 |
| 可信代理 | 在既有可信 IP 外支援明確 CIDR allowlist，固定 Compose 代理子網範圍，API host port 維持不發布。只信任 Compose 固定的代理子網段或使用者明確設定的 CIDR。不得自動信任所有私有網段，例如 `10.0.0.0/8`、`172.16.0.0/12`、`192.168.0.0/16`。驗收私有網段內其他來源偽造的 `X-Forwarded-*` 不會被採用，並核對 HTTPS scheme、SignalR Origin、限流所用的真實 Client IP，以及代理外部無法存取 `/health/*`。 |
| Migration 與啟動 | 獨立一次性 Job，DB 未就緒時有限等待；Job 失敗不得啟動不相容 API。API 啟動時以 DML 帳號唯讀檢查 schema／遷移歷史，確認不相容記 Critical 並以非零碼退出；短暫無法連線先有限重試。開工時定義高於應用版本的 schema 處置。確認已遷移、未遷移、暫時不可達、測試隔離 schema，以及 EF Core 10／現行 Npgsql 的並行 migration lock 行為。DDL 帳號與 API 帳號最小權限分離，readiness 只做低成本 DB 探針。 |
| Compose | 正式設定放 `deploy/`，根目錄測試用 `compose.yaml` 保留。初始化用 `.env.example` 說明密碼、主機、TLS、持久化設定，未提供必要設定須拒絕啟動。初始化後支援 `docker compose up -d`。服務使用 stdout／stderr 與有界輪替，例如 `local` driver 的 `max-size: 10m`、`max-file: 5`，驗證目標 Docker／NAS 相容性並提供替代 driver。日誌與 DB 資料各有保存期限。 |

CSP 初稿為 `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`。Vite 將 logo SVG 內嵌供 `mask-image` 使用，需保留 `img-src` 的 `data:`。依部署來源及瀏覽器驗證 `connect-src` 是否須明確列出同源 WSS。

## 13. S8：負載證據、驗收與文件收尾

交付可重跑負載情境／版本與硬體設定、結果報告、查詢計畫／主要瓶頸、最終候選設定調整與已知限制；補齊操作／本機啟動及測試說明。使用既定單實例，不因測試數字就新增 Redis 等基礎設施。

在隔離的近正式容器環境使用 `dotnet-counters` 即時觀察內建與自訂 meter，並以獨立的 .NET Aspire Dashboard 容器接收 OTLP，查看 trace、日誌及指標。沿用 §10 的官方映像版本固定、loopback 綁定、存取驗證及資料遮蔽。Dashboard 資料只在記憶體，須於停止容器前整理驗收證據。Dashboard 放置方式（獨立 Compose 檔或 profile 等）及是否將 `dotnet-counters` 加入 `dotnet-tools.json`，到 S8 開工時另提方案。

驗收情境：

| 情境 | 必須記錄／驗證 |
| --- | --- |
| 1,000 連線／約 200 房／典型 5 人 | 公開建房／加入暖機及升載過程、metadata 約 50～67 req/s、實際寫入＋所有快照補償；不能忽略衍生讀取 |
| 同一 NAT／公司／學校出口 | 既有千條連線與新加入速率分開測；合法成功率／429，不能只用不同 IP 模擬就聲稱支援 NAT |
| 熱房／最大文字 | 高集中度多人新增刪除、300 筆×8 KiB；觀察整份快照重抓的頻寬、DB 讀取、UI 收斂；據此決定是否以新 ADR 恢復分頁 |
| 重連／通知遺漏 | 大量同時重連 jitter、全部或部分提示遺漏、回前景補償；修復主目標 20～30 秒、健康情況約 60 秒容許上限 |
| 濫用／資源／清理 | 分散錯碼、partition 耗盡、pool／CPU／記憶體／磁碟壓力、過期 backlog 與清理保留資源。在近正式容器環境使用 OTel 指標，量測重房（1,000 Participants＋300 筆 × 8 KiB）的最壞批次耗時、縮批後的吞吐與失效至刪除延遲，驗證是否達 10 分鐘目標。 |

候選基準為 app／DB 各 4 vCPU／8 GiB、每房初始 50 筆／平均 1 KiB、全站每秒 20 次平衡新增／刪除、30 分鐘，再做階梯與邊界；REST p95 ≤ 500 ms、提交到畫面 p95 ≤ 2 秒仍待量測核定。測試報告一併列合法成功率、429／5xx、實際接收通知／同步量、修復分布、DB lock／pool 等待及資源，不用拒絕正常流量粉飾達標。

S8 出口：需求 AC-01～AC-15 有可追溯證據。若未達 1,000 或時間目標，如實標示未達與瓶頸／調整提案，不標成已支援；涉及架構改動另 Review。完整 Build／Lint／相關 Tests 通過後，無新修改或疑慮不重複擴大測試。

## 14. Seq／AWS

- Seq 為選配 `monitoring` Compose profile，固定官方映像版本、持久化資料、預設 host 連接埠綁 `127.0.0.1`。初始管理員密碼透過 `.env`，驗證初次啟動、持久卷重啟、保留期限及管理存取。啟用前由使用者確認官方授權與 EULA。Serilog Seq sink 可設定開關，保留 Console，驗收 Seq 離線時 Pasto 仍能啟動及服務。
- AWS 交付部署建議文件。ECS 用 `awslogs`，EC2＋Compose 用 agent 或 forwarder，由基礎設施管理 CloudWatch、最小權限 IAM、保存期限與警報。驗收文件涵蓋收集路徑、URL／日誌脫敏配置及故障行為，應用程式維持 Console 輸出。

## 15. 下一個實作任務與完成回報

S6 主資料、房碼冷卻回收與 SignalR 連線清理已完成審查並提交至工作分支。下一步先完成 Logging 階段方案確認。後續階段依 [ADR 0004](decisions/0004-observability-and-deployment.md) 與 [TODO.md](../TODO.md) 接續。實際測試與失敗紀錄見 [testing.md](testing.md)。
Phase 2 Object Storage／Metadata／檔案權限／可重試清理僅留在架構方向，不是本計畫交付，不建立 RoomFiles、IFileStorage 或檔案 API。
