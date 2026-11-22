# Pasto — 開發進度與下一步

更新日期：2026-11-21（Asia/Taipei）。這是目前狀態的入口。開工前先讀 [AGENTS.md](AGENTS.md)，並以實際 Git 狀態核對本檔。詳細設計與驗證紀錄以連結文件為準。

## 目前位置

| 項目 | 狀態 |
| --- | --- |
| S3 交付分支 | `feature/s3-02-item-snapshot`，由 `feature/s3-01-item-write-api` 的 `df2510d` 接續 |
| S3 開發基準 | `main` 的 `9eace5f`，PR #5：完整 S2 |
| S3-01 功能提交 | `408593e`：文字新增／刪除 API 與 Participant 寫入配額 |
| S3-01 P3 修正 | `df2510d`：補強連線池設定重設驗證 |
| S3 交付 | [PR #6](https://github.com/ARueiz/Pasto/pull/6) 包含 S3-01 與 S3-02，目標分支為 `main`，合併狀態以 PR 為準 |
| S3-02 審查 | 獨立 review 已完成，無阻擋問題，P3 已補文件。審查範圍 `df2510d..a05d87f` |
| S4 交付 | `feature/s4-clipboard-ui` 已由使用者透過 [PR #7](https://github.com/ARueiz/Pasto/pull/7) 合併至 `main@00c2183`。完整獨立 review 已完成，P2 已修正並通過回歸 |
| S5 實作分支 | `feature/s5-signalr-sync`，由包含 S4 合併點的 `main@efbcb73` 建立 |
| S5 合併 | [PR #8](https://github.com/ARueiz/Pasto/pull/8) 已於 2026-11-20 合併至 `main@98424e6`（Asia/Taipei） |
| S6 工作分支 | `feature/s6-room-cleanup`，由包含 S5 合併與交付紀錄的本機 `main` 建立 |
| S6 第一項提交 | `8e3d1bf`，主資料清理、Worker 收尾及 P3，審查無阻擋問題 |
| S6 第二項提交 | `a394f3c`，房碼回收、SignalR 連線清理及追蹤滿載 P2，審查無阻擋問題 |
| S6 合併 | [PR #9](https://github.com/ARueiz/Pasto/pull/9) 已於 2026-11-21 合併至 `main@5ff602d`（Asia/Taipei） |
| 可觀測性合併 | [PR #10](https://github.com/ARueiz/Pasto/pull/10) 已於 2026-11-21 合併至 `main@6200a96`（Asia/Taipei） |
| S7 合併 | [PR #11](https://github.com/ARueiz/Pasto/pull/11) 已於 2026-11-21 合併至 `main@cd5b589`（Asia/Taipei） |
| Deployment 合併 | [PR #13](https://github.com/ARueiz/Pasto/pull/13) 已合併至 `main@58c3c9f`，自架部署與每來源限流驗收完成 |

開工前以 `git status` 確認工作目錄狀態，不覆蓋未提交的修改。不得自行推送或合併，除非使用者明確授權。

## 階段進度

- [x] S1：正式持久層、交易核心、併發與期限邊界，已合併。
- [x] S2：建房、Join、Bearer、me、metadata、關房 API，已合併；使用者已確認測試審查 OK。
- [x] S3-01 實作：POST／DELETE items；新增與刪除配額分開，DELETE room 與刪除文字共用刪除配額。
- [x] S3-01 review finding 修正：fixture 改用自有、有上限的 NpgsqlDataSource pool，先關 pool 再清獨立 schema；新增 3 例回歸測試。
- [x] S3-01 修正的獨立 review／審查方驗證：無 P0～P2；非阻擋 P3 已補強測試。
- [x] S3-02 實作：整份文字快照列表、唯讀交易與既有讀取配額。
- [x] S3-02 獨立 review：無阻擋問題，P3 補記最壞回應大小估算與後續量測。
- [x] S4 實作：房間身分與分享、聊天室文字操作、搜尋、狀態提示、手動刷新與主題切換。
- [x] S4 實測調整：定時複製提示、輸入框圓角焦點、房主選單只保留關房。
- [x] S4 實測調整獨立 review（`7d8960e`）：無 P0～P2。非阻擋 P3：浮動複製提示的垂直位置寫死 128px，放大字級或展開 QR 面板時可能疊在其他元素上。
- [x] S4 MacBook 經 Dev Tunnels HTTPS 實機驗證：使用者確認複製直接成功、提示會自動消失、房主選單不顯示離開。
- [x] S4 完整獨立 review（`dbfcb24..7d8960e`）：無阻擋問題，有 1 個 P2 和 3 個 P3。身分恢復、切房時晚到的回應（200 和 401）、純文字渲染、憑證不進 URL 或 QR Code，都已經驗證。
- [x] S4 P2 修正：`CopyFallback` 只在開啟或文字變更時聚焦並選取。新增 Edge 回歸測試，確認手動複製框開著時，輸入框與搜尋框仍能逐字輸入並保有焦點。
- [x] S4 合併：PR #7 已合併，本機 `main` 已同步。
- [x] S5 實作：同房 SignalR 訂閱、提交後通知、整份快照同步及斷線重連。
- [x] S5 審查修正：P2 × 2（Hub 失敗時 REST 備援、寫入不受同步狀態限制）、P3 × 1（以提交版本判定送出後捲動）。
- [x] S5 房間介面：智慧捲動、「跳到最新」、訪客名稱配色，以及 Pasto 分頁圖示網址更新。
- [x] S5 第三輪：修正平滑捲動途中快照更新及首連狀態兩項 P3，加入五碼作者識別與連續訊息分組。
- [x] S5 獨立審查，審查方回報無阻擋問題。跨午夜標籤 P3 已修正，平滑捲動中斷已補 Edge 測試。 [審查交接與結果](docs/s5-review.md)
- [x] S5 合併。[PR #8](https://github.com/ARueiz/Pasto/pull/8) 已合併至 `main@98424e6`。
- [x] 依 AGENTS.md §10 更新 README 與 TODO，記錄合併 PR、S5 交付及 S6。
- [x] S6：到期／關房資料清理。
  - [x] 主資料清理實作與專項驗證。分批實體刪除失效房間，跳過鎖定房間並保留房碼。批次逾時時減半，最小到 1，滿批成功後逐步恢復。Worker 記錄連續失敗輪數並繼續下一輪。
  - [x] 收尾 P3 修正。整輪預算用完時若已有房間刪除成功提交，記錄 `6006`、歸零連續失敗輪數；無進度仍記錄 `6002` 並累加。
  - [x] 房碼保留紀錄回收與 SignalR 連線清理已完成審查，無阻擋問題。
  - [x] 第二項 P2。追蹤滿載時允許訂閱，Worker 彙總 `6009`。
- [x] Logging：結構化日誌與 Hub 例外。
- [x] Health Checks：存活、DB 就緒與 Worker 狀態。
- [x] OpenTelemetry：指標、Tracing 與選配 OTLP。
- [x] S7：防護、資源與濫用處理。PR #11 已合併。
  - [x] 啟動盤點。使用者確認 Dev Tunnel 即時同步測試通過。已建立 SignalR、日誌、遙測與受保護 REST 的驗證基線。
  - [x] S7-1 憑證查詢防護：查無憑證才扣來源失敗額度，REST 與 SignalR 共用 DB 保護底線。查無快取讓重複舊憑證直接回 401，跨房 REST 請求扣 Participant 讀取額度。P1 的 F1～F4 複核通過，無新 findings。已提交至 `feature/s7-credential-admission@0811f17`。
  - [x] S7-2 公開加入與猜碼防護。重犯退避、冷卻表滿載與同 NAT 30 人加入的修正已通過複核。
  - [x] S7-3 SignalR 常駐連線配額、negotiate 速率與 `SubscribeRoom` 限流。WebSocket 強制使用協商取得的 token，Origin 驗收與獨立複核通過。
  - [x] S7-4 HTTP 並行、過載 admission 與 partition 上限。慢速 I/O、同 NAT 30 人重試及 WebSocket 閒置驗收已通過。
- [x] Deployment：同源靜態檔、TLS、可信代理、Migration Job 與自架 Compose，已透過 PR #13 合併。
- [ ] S8：壓測與收尾。
- [ ] Seq／AWS：選配監控與 AWS 部署建議。

## 最新驗證

S7 整合審查修正後，專項 49 例與完整 PostgreSQL 回歸 385 例均通過。前端最近一次 Edge 回歸 44 例通過。 [命令與 TRX](docs/testing.md#s7-整合審查-p3-修正2026-11-21)

S7-4 專項 34 例、前三批專項 43 例、受保護 REST／SignalR／日誌／遙測 144 例、完整 PostgreSQL 374 例與 Edge 44 例均通過。 [命令與 TRX](docs/testing.md#s7-4-http-處理名額與過載重試2026-11-21)

S7-3 審查修正後，專項 14 例、前兩批 29 例、受保護 REST 122 例、SignalR／日誌／遙測 22 例及完整 PostgreSQL 回歸 361 例均通過。沿用上一輪 Edge 38 例。 [命令與 TRX](docs/testing.md#s7-3-p2-與-p3-審查修正2026-11-21)

S7-2 審查修正後，專項 14 例、S7 第一批 15 例、受保護 REST 122 例、SignalR／日誌／遙測 22 例及完整 PostgreSQL 回歸 347 例均通過。 [命令與 TRX](docs/testing.md#s7-2-p1-與-p3-審查修正2026-11-21)

S7 第一批 P1 修正後，專項 15 例、受保護 REST 122 例、SignalR／日誌／遙測 22 例、完整 PostgreSQL 回歸 333 例均通過。前端沿用上一輪 Edge 32 例。 [重現、命令與 TRX](docs/testing.md#s7-第一批-p1-審查修正2026-11-21)

S7 第一批完整 PostgreSQL 回歸 332 例與 Edge 32 例通過。受保護 REST 122 例、SignalR／日誌／遙測 22 例及第一批專項 14 例均通過。 [命令與 TRX](docs/testing.md#s7-第一批憑證失敗計費2026-11-21)

S7 啟動基線：Release 建置與格式檢查通過。SignalR、日誌與遙測 22 例，受保護 REST 122 例，均通過且無失敗或略過。TRX 為 `s7-kickoff-baseline.trx` 與 `s7-rest-baseline.trx`。

Logging、Health Checks 與 OpenTelemetry 已在 `feature/observability` 驗證。完整 PostgreSQL 回歸 313 例通過；另驗證獨立 Aspire Dashboard 回讀 OTLP 及 `dotnet-counters` 即時指標。 [測試紀錄](docs/testing.md#logginghealth-checks-與-opentelemetry2026-11-21)

S6 第二項 P2 的 Release Build、格式檢查、PostgreSQL／SignalR 專項 27 例與完整回歸 307 例通過。 [測試紀錄](docs/testing.md#s6-訂閱追蹤滿載-p22026-11-21)

S6 第二項的 Release Build、格式檢查與完整 PostgreSQL／SignalR 回歸 305 例通過。 [測試紀錄](docs/testing.md#s6-房碼回收與-signalr-連線清理2026-11-20)

S6 整輪預算 P3 修正的 Release Build、格式檢查、真 PostgreSQL 專項 12 例及完整回歸 296 例通過。 [測試紀錄](docs/testing.md#s6-整輪預算-p3-驗證2026-11-20)

S6 Worker 收尾的 Release Build、格式檢查、真 PostgreSQL 專項 10 例及完整回歸 294 例通過。 [測試紀錄](docs/testing.md#s6-主資料清理收尾驗證2026-11-20)

S6 主資料清理的 Release Build 與格式檢查通過，真 PostgreSQL 專項 6 例通過，最終完整回歸 290 例通過。首輪既有快照測試的索引斷言失敗與基準診斷見 [測試紀錄](docs/testing.md#s6-主資料清理驗證2026-11-20)。

2026-11-20 合併前修整的前端 Lint、Build 與完整 Edge 28 例通過，涵蓋跨午夜作者標籤與鍵盤中斷平滑捲動。 [測試紀錄](docs/testing.md#s5-合併前修整驗證2026-11-20)

S5 第三輪的 Release Build、格式檢查、前端 Lint 與 Build、完整 PostgreSQL 284 例、Edge 26 例通過。雙 Edge 經真實後端驗證房主／訪客標籤、同一訪客跨畫面代碼一致及重新整理恢復。見 [測試紀錄](docs/testing.md#s5-第三輪審查與作者代碼驗證2026-11-19)。

S5 房間介面調整後，前端 Lint、Build、完整 Edge 20 例與完整 PostgreSQL 整合測試 282 例通過。獨立 Edge 工作階段已驗證正式建置可取得 Pasto 的 SVG 與 ICO 分頁圖示。

S5 審查修正後，前端 Lint、TypeScript／Build 與完整 Edge 18 例通過。兩個 Edge 工作階段在 Hub 協商回 403 時，以實際 REST 完成讀取、重新整理、跨工作階段補查及寫入。細節見 [測試紀錄](docs/testing.md#s5-審查修正驗證2026-11-19)。

S5 的 Release Build、格式檢查、前端 TypeScript／Lint／Build 均通過。完整 PostgreSQL 整合測試 282 例、Edge 自動化測試 14 例通過。兩個 Edge 工作階段透過實際 WebSocket 驗證新增、刪除、重連、關房及遺漏通知修復。測試記錄見 [測試紀錄](docs/testing.md#s5-signalr-與整份快照同步驗證2026-11-19)。

S4 P2：修正前逐字輸入 `Pasto clipboard` 時只留下 `P`，已由新增測試重現。修正後 TypeScript、Lint、Build 通過，完整 Edge 10 例通過。

S4 實測調整的 TypeScript、Lint、Build 與 Edge 瀏覽器測試 9 例通過。Edge 使用模擬 API，已擷取 360px 淺色、1280px 深色畫面。先前後端回歸 278 例通過，本輪只有前端與文件變更。開發工具稽核限制見 [S4 前端 API](docs/s4-api.md)。

審查方（Claude）2026-11-19 在 `7d8960e` 獨立重跑：TypeScript、Lint、Build 通過，Edge 9 例全數通過。部署環境實測（2026-11-18，區網 Vite＋正式後端）：API 授權邊界 21 項符合預期，包括跨房 token、訪客刪除或關房、關房後 404。區網 HTTP 不是 secure context，Clipboard API 不可用，因此裝置測試改用 Dev Tunnels HTTPS（private，限 owner）。MacBook 實機驗證已由使用者確認通過；iPhone 掃 QR 尚未測試。

S3-02 審查方在 `a05d87f` 重跑相關 12 例及完整兩輪各 278 例，全部通過。Release Build、格式檢查通過，殘留 schema 0。TRX 核對結果見 [測試紀錄](docs/testing.md)。

審查方（Claude）獨立重跑，2026-11-18，`da98a83`：

- Release Build 0 warning／0 error；format verify 通過。
- 相關 `TestDatabasePoolingTests` 3 passed；同一命令緊接兩輪完整測試各 **269 passed、0 failed、0 skipped**。
- 殘留 schema 0。未重測 TIME_WAIT，以下取樣數字仍為實作方紀錄。

實作方結果：

- Release Build：0 warning、0 error；format verify、diff check 通過。
- 相關連線池／併發測試：11 例通過。
- 同一命令緊接兩輪完整測試：各 **269 passed、0 failed、0 skipped**。命令為 `dotnet test Pasto.slnx -c Release --no-build --no-restore`（各輪加 TRX logger）；需先啟動 Docker Compose 並設定 `PASTO_TEST_CONNECTION_STRING`，步驟見 [README](README.md)。
- TIME_WAIT 取樣：修改前單輪峰值 4,837；修改後第一輪 544，緊接第二輪累積峰值 1,088。
- 測試後殘留 schema：0。
- 先前 socket 連線失敗、測試修正及取樣限制均保留於 [testing.md](docs/testing.md)。未刻意重現埠耗盡，不宣稱已排除所有根因。

目前不需要使用者額外手動測 API。若後續需要，應提供具體步驟與預期結果。

## 下一個工作

Deployment 已透過 [PR #13](https://github.com/ARueiz/Pasto/pull/13) 合併至 `main`。下一階段是 S8 壓測與文件收尾，涵蓋 1,000 條連線、同一 NAT、熱房、大量重連與過期清理。 [部署驗收紀錄](docs/testing.md#deployment2026-11-22) · [S8 實作計畫](docs/sprint-1-implementation-plan.md#13-s8負載證據驗收與文件收尾)

S3 已由 [PR #6](https://github.com/ARueiz/Pasto/pull/6) 合併。S3-01 的非阻擋 P3 修正保留在 `df2510d`。S3-02 的最壞回應大小估算和審查證據見 [測試紀錄](docs/testing.md)。

## 後續必須保留的事項

- S6 第二項非阻擋 P3：`6009 RoomConnectionTrackingFull` 目前隨清理 Worker 彙總。停用 Worker 時沒有摘要。後續確認是否需要與 Worker 狀態獨立的有界摘要，保留每期間最多一筆的日誌節流。

- S7 已驗證全站 SignalR 連線配額及每個 Participant 3 條連線。S6 的 1,500 條上限只限制追蹤表記憶體，滿載仍可訂閱。正式反向代理的 token 遮蔽已在 Deployment 驗收。 [部署驗收紀錄](docs/testing.md#deployment2026-11-22)
- 後續可評估作者代碼撞碼處理。目前接受五碼相同的作者在連續顯示時可能被分在同組。
- S7／S8：同 NAT 共用失敗額度、跨房請求扣自身讀取額度。大量 /64 或在途併發仍可耗盡全站 DB 保護底線。查無快取與來源失敗桶已限量，既有框架 partition 的記憶體峰值與重連尖峰留待 S8 量測。 [第一批風險](docs/s2-api-identity.md#s7-第一批失敗計費)
- S7-4：DB 底線、建房及 Join 的來源速率桶共 10,000，已驗證 negotiate 的來源表 5,000，滿載時表外來源走各自的全站桶。Participant 表上限 50,000 個 ID，滿載時新身分沿用適用的 DB 底線、每房配額與 in-flight，並記錄節流警告。
- S7-4／S8：快照寫出前已釋放 HTTP 處理名額，慢讀仍可能占用記憶體。S8 量測約 14.8 MB 最大回應情境的程序與代理記憶體峰值。
- S7：持有分享連結者可反覆 Join 耗盡 1,000 個生命週期 Participant 名額。
- S7-2／S8：攻擊者以大量 /64 輪流送錯碼，可在恢復期反覆觸發壓力，使全站長期處於壓力模式。壓力期間若攻擊者持續送出每秒 2 次以上的請求，大部分合法 Join 會被拒，使用者需要重試。S8 量測壓力模式下合法 Join 的成功率與延遲。
- S7／S8：實測以控制字元填滿 300 筆時的 JSON 回應大小與讀取頻寬。S8 另測 1,000 條連線同時重抓。
- **S7 防護完成前不得公開部署**。任何不受信任的裝置或使用者可連線的環境都算公開部署，包括可由訪客 Wi-Fi 或 BYOD 存取的內網。受信任內網仍需驗證、限流與敏感資料保護。 [ADR 0004](docs/decisions/0004-observability-and-deployment.md)
- S4 P3：在選單按 Esc，或用 Esc 取消確認對話框之後，焦點會掉到 `<body>`。建議把焦點還給「更多選項」按鈕。
- S4 P3：浮動複製提示的垂直位置寫死在 128px（`4 * --space-8`）。放大字級或展開 QR 面板時，提示會疊在其他元素上。它設了 `pointer-events: none`，不會擋到點擊。
- Deployment 以自架 Compose 與 ASP.NET Core 同源靜態檔為基準。CSP、WSS、安全 header、快取與代理驗收見 [Deployment 計畫](docs/sprint-1-implementation-plan.md#12-deployment)。

## 文件入口與維護

- [AGENTS.md](AGENTS.md)：長期開發規範。
- [README](README.md)：本機環境、Docker Compose 與測試指令。
- [S3 工作項](docs/s3-items.md)：當前 patch 範圍、契約與驗收。
- [測試紀錄](docs/testing.md)：實測證據、失敗與環境資訊。
- [分階段計畫](docs/sprint-1-implementation-plan.md)、[架構](docs/architecture.md)、[需求](docs/requirements.md)、[ADR 0003](docs/decisions/0003-mvp-scope-reduction.md)：詳細設計與既定取捨。

每個 patch 提交、review 結果或合併完成後更新本檔；保留目前狀態與下一步，歷史細節移至對應文件，避免維護多份完整設計或測試日誌。
