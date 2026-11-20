# Pasto — 開發進度與下一步

更新日期：2026-11-20（Asia/Taipei）。這是目前狀態的入口。開工前先讀 [AGENTS.md](AGENTS.md)，並以實際 Git 狀態核對本檔。詳細設計與驗證紀錄以連結文件為準。

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
- [ ] S6：到期／關房資料清理。
  - [x] 主資料清理實作與專項驗證。分批實體刪除失效房間，跳過鎖定房間並保留房碼。批次逾時時減半，最小到 1，滿批成功後逐步恢復。Worker 記錄連續失敗輪數並繼續下一輪。
  - [x] 收尾 P3 修正。整輪預算用完時若已有房間刪除成功提交，記錄 `6006`、歸零連續失敗輪數；無進度仍記錄 `6002` 並累加。
  - [ ] 房碼保留紀錄回收與 SignalR 連線清理。
- [ ] Logging：結構化日誌與 Hub 例外。
- [ ] Health Checks：存活、DB 就緒與 Worker 狀態。
- [ ] OpenTelemetry：指標、Tracing 與選配 OTLP。
- [ ] S7：防護、資源與濫用處理。
- [ ] Deployment：同源靜態檔、TLS、可信代理、Migration Job 與自架 Compose。
- [ ] S8：壓測與收尾。
- [ ] Seq／AWS：選配監控與 AWS 部署建議。

## 最新驗證

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

2026-11-20，S6 第一項已在 `feature/s6-room-cleanup` 提交至 `8e3d1bf`，可觀測性與部署文件已更新。下一個 S6 工作項目先提出房碼保留紀錄回收與 SignalR 連線清理方案。 [ADR 0004](docs/decisions/0004-observability-and-deployment.md) · [實作計畫](docs/sprint-1-implementation-plan.md)

S3 已由 [PR #6](https://github.com/ARueiz/Pasto/pull/6) 合併。S3-01 的非阻擋 P3 修正保留在 `df2510d`。S3-02 的最壞回應大小估算和審查證據見 [測試紀錄](docs/testing.md)。

## 後續必須保留的事項

- S6：由 Worker 接續處理到期與關房後的連線清理。S5 的訂閱複驗與 REST 請求已拒絕失效房間。
- S7：驗證 Hub 協商與連線配額、日誌中的 token 遮蔽與正式反向代理。
- 後續可評估作者代碼撞碼處理。目前接受五碼相同的作者在連續顯示時可能被分在同組。
- S7：少數來源可耗盡共用的驗證前 read admission，影響讀取，也會阻擋新增文字、刪除文字與關房。Participant 寫入配額不會緩解此入口風險。
- S7：持有分享連結者可反覆 Join 耗盡 1,000 個生命週期 Participant 名額。
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
