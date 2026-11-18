# Pasto — 開發進度與下一步

更新日期：2026-11-19（Asia/Taipei）。這是目前狀態的入口；開工前先讀 [AGENTS.md](AGENTS.md)，並以實際 Git 狀態核對本檔。詳細設計與驗證紀錄以連結文件為準。

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
| 當前交付點 | 本機 `main` 已快轉至 `00c2183`。S5 尚未開始，預計 2026-11-20（Asia/Taipei）接續 |

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
- [ ] S5：SignalR 通知與同步驗收。
- [ ] S6：到期／關房資料清理。
- [ ] S7：防護、資源與濫用處理。
- [ ] S8：壓測與收尾。

## 最新驗證

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

2026-11-20 從 `main@00c2183` 建立 S5 功能分支，依 [S5 實作計畫](docs/sprint-1-implementation-plan.md#6-s5signalr-與方案-a-同步) 先處理伺服器端的同房訂閱授權與交易提交後的 `RoomChanged` 通知，再接前端快照同步與斷線重連。S4 的 P3 和待補回歸測試保留下方。本輪只同步 `main` 與更新交接文件。

S3 已由 [PR #6](https://github.com/ARueiz/Pasto/pull/6) 合併。S3-01 的非阻擋 P3 修正保留在 `df2510d`。S3-02 的最壞回應大小估算和審查證據見 [測試紀錄](docs/testing.md)。

## 後續必須保留的事項

- S5：新增／刪除／關房的通知只能在提交成功後發送；依既定同步契約驗收，重新連線由 Server 重新同步。
- S7：少數來源可耗盡共用的驗證前 read admission，影響讀取，也會阻擋新增文字、刪除文字與關房。Participant 寫入配額不會緩解此入口風險。
- S7：持有分享連結者可反覆 Join 耗盡 1,000 個生命週期 Participant 名額。
- S7／S8：實測以控制字元填滿 300 筆時的 JSON 回應大小與讀取頻寬。S8 另測 1,000 條連線同時重抓。
- **S7 防護補齊前不得公開部署**；已記錄風險不代表已緩解，不自行調整既定配額。
- S5：補 AC-08 的自動化回歸測試，驗證換房、離開、關房之後晚到的回應不會污染畫面。目前只有審查方用 Playwright 探測腳本驗證過，e2e 裡沒有。
- S5（S4 P3）：`api.ts` 的 `fetch` 沒有設定逾時。請求一直沒有回應時，「重新整理」會被停用、輸入框變成唯讀，畫面上也沒有任何提示，要等瀏覽器自己逾時才能操作。建議用 `AbortSignal.any([signal, AbortSignal.timeout(...)])`，逾時後走既有的「結果不確定」流程。
- S4 P3：在選單按 Esc，或用 Esc 取消確認對話框之後，焦點會掉到 `<body>`。建議把焦點還給「更多選項」按鈕。
- S4 P3：浮動複製提示的垂直位置寫死在 128px（`4 * --space-8`）。放大字級或展開 QR 面板時，提示會疊在其他元素上。它設了 `pointer-events: none`，不會擋到點擊。
- S7／部署（AWS）：要決定前端由 CloudFront＋S3 提供，或由後端直接提供建置好的檔案。這屬於新增基礎設施，要先提方案、等使用者確認。部署時要設定以下 header：
  - CSP 初稿：`default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`。其中 `img-src` 必須允許 `data:`，因為 Vite 把 logo 的 SVG 內嵌成 `data:` URI，用在 `mask-image` 上。
  - `Referrer-Policy: no-referrer`、HSTS、`X-Content-Type-Options: nosniff`。
  - 快取：`index.html` 設 `no-cache`；帶 hash 的 `assets/` 設長期快取。
  - 驗證方式：用 `vite preview` 套上這組 header 跑 e2e。S5 加入 SignalR 後，要再確認 `connect-src` 是否涵蓋 `wss://`。

## 文件入口與維護

- [AGENTS.md](AGENTS.md)：長期開發規範。
- [README](README.md)：本機環境、Docker Compose 與測試指令。
- [S3 工作項](docs/s3-items.md)：當前 patch 範圍、契約與驗收。
- [測試紀錄](docs/testing.md)：實測證據、失敗與環境資訊。
- [分階段計畫](docs/sprint-1-implementation-plan.md)、[架構](docs/architecture.md)、[需求](docs/requirements.md)、[ADR 0003](docs/decisions/0003-mvp-scope-reduction.md)：詳細設計與既定取捨。

每個 patch 提交、review 結果或合併完成後更新本檔；保留目前狀態與下一步，歷史細節移至對應文件，避免維護多份完整設計或測試日誌。
