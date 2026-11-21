# S3：文字 API 與整份快照

基準 main `9eace5f`（S2 已合併）。依 architecture §4／5、requirements AC-03～AC-07 與 ADR 0003 實作；不改 schema、不加套件、不加入分頁或單筆 GET。各 patch 完成後交獨立 review。

## S3-01：文字寫入與配額

- POST /api/rooms/{roomId}/items 接 AddItem，201 回 item 與 committedVersion（版本皆字串）。原文不 trim；空白回 EmptyContent、超 8192 UTF-8 bytes 回 TextTooLarge、NUL／無效 Unicode 回 InvalidRequest。JSON body 上限沿用 32 KiB。
- DELETE /api/rooms/{roomId}/items/{itemId} 接 DeleteOwnItem，200 回 changed 與 committedVersion；不存在不改版本，房主無刪除他人文字特權。
- Owner 只能取自驗證身分，body 僅允許 content（拒絕 Owner／Creator 等未知欄位）。S7 第一批起，通過綁定的跨房請求先扣 Participant 讀取配額，再回 403，配額耗盡回 429。同房業務有效性與授權仍由既有鎖內流程重驗。
- POST／DELETE items 與 DELETE room 的憑證查詢和讀取、Hub 共用 DB 保護底線。已驗證同房新增套用 Participant 每分鐘 30／burst 10，刪除文字與關房共用 Participant 每分鐘 60／burst 10。通過 JSON 綁定與同房檢查後，業務成功／失敗均消耗對應額度，不排隊，429 帶 Retry-After 與 no-store。
- 每房寫入 token bucket、分類並行與部署資源 admission 依 ADR 0003 留 S7，不在本 patch 擴大範圍。S7 既有讀取／Join 濫用風險持續存在，補齊前不公開部署。
- S7 的來源失敗桶與查無快取可降低假憑證與舊分頁重試對共用底線的消耗。大量 /64 或瞬時併發仍可用盡全站底線，共用 NAT 仍可能被同來源失敗擋下。跨房請求可耗盡自己身分的讀取額度，同房寫入仍可使用 Participant 寫入額度。 [S7 前綴、快取與狀態上限](s2-api-identity.md#s7-第一批失敗計費)
- 測試：HTTP 身分／權限、輸入與 byte 邊界、內容保留、計數／版本、無操作刪除、限流順序與共用性、提交失敗／取消、不重試。既有 S1 併發／容量測試與 S2 body 邊界測試納入回歸。

## S3-02：整份快照列表

- GET /api/rooms/{roomId}/items 使用同一 read admission 與 Participant 讀取配額；唯讀 REPEATABLE READ 交易中檢查有效性與同房，回全部 items、itemCount、snapshotVersion、expiresAt。
- CreatedVersion DESC、Id DESC 排序；以同一 snapshot 取得 Room 與 items，版本字串化。提供 isOwn／canDelete，不以 UI 旗標取代後端授權。
- 測空房、他房隔離、失效、寫入競爭、一致快照、穩定排序及 300×8 KiB 完整回傳，量測回應 bytes 並檢查查詢索引。前置資料走正式新增 use case。

S5 再接交易成功後 SignalR 通知與既定同步驗收條件。本輪不推送或合併。

## S3-01 交付紀錄

已實作 ItemEndpoints、ParticipantWriteLimiter／Filter，並接入既有關房 endpoint；錯誤碼依契約細分 EmptyContent／TextTooLarge。AddedItem 補上由鎖內 DB 時間取得的 CreatedAt，201 無須提交後再查一次 DB。沒有改變既有新增／刪除／關房交易邏輯。

新增 38 例；最終相關 38 passed，完整 266 passed／0 failed／0 skipped；Release Build 0 warning／0 error，format verify 與 diff check 通過，殘留 schema 為 0。先前兩次完整測試曾因 socket 連線失敗未全綠；完整錯誤與後續驗證紀錄見 testing.md，不省略失敗結果。

本 patch 已完成獨立 review（產品程式無阻擋問題，P2 測試連線池於 `ad5ff82` 修正，見下節）；S3-02 未開始。目前不需要額外手動 API 測試；UI 操作與即時同步驗收仍留 S4／S5。

## S3-01 審查修正

P2 測試連線池：fixture 改為自有 NpgsqlDataSource 與有界 pool，結束時先關 pool 再清 schema，保留原隔離及正式 use case 建資料方式。新增 3 例驗證重用、session 重設、獨立連線與清理；連續兩輪完整測試各 269 passed／0 failed／0 skipped，TIME_WAIT 取樣峰值由基準單輪 4837 降為修正後單輪 544，緊接第二輪累積峰值 1088，殘留 schema 為 0。完整失敗、取樣限制與 TRX 紀錄見 testing.md。僅修正測試設施，沒有變更產品程式；S3-02 未開始。

## S3-01 修正的獨立 review（2026-11-18）

審查方（Claude）審查 `408593e..ad5ff82`：無 P0／P1／P2，無 Blocking Issue。獨立重跑 Release Build 0 warning／0 error、相關 3 passed、同一命令緊接兩輪完整測試各 269 passed／0 failed／0 skipped，殘留 schema 0，format verify 通過；未重測 TIME_WAIT。留有 1 個非阻擋 P3：連線池重用測試中的 `SET LOCAL lock_timeout` 因 rollback 即還原，無法驗證 pool reset，尚未修正。細節見 testing.md。

P3 後續依使用者指示於 `df2510d` 改用交易外的 `SET lock_timeout`，相關 3 例和完整 269 例皆通過。

## S3-02 交付紀錄

已實作 GET `/api/rooms/{roomId}/items`。沿用讀取 admission、Participant 配額與房間有效性檢查。房間統計與文字清單在同一個唯讀 REPEATABLE READ 交易中讀取，版本以字串回傳。`isOwn` 與 `canDelete` 依每筆文字的擁有者判定。

新增 9 例 PostgreSQL 整合測試，涵蓋授權、失效、排序、限流、並行提交及滿房內容完整性。獨立 review 已完成，無阻擋問題。非阻擋 P3 已補上控制字元使滿房 JSON 回應約 14.8 MB 的估算，實測列入 S7／S8。查詢計畫、量測與審查證據見 [testing.md](testing.md)。
