# S1-04：併發交易核心

本階段分為新增、刪除、關房三個 patch，沿用現有 schema／Migration，不新增套件。
從已 pull 的 main `1ad0e2b`（PR #2 合併結果）建立 `feature/s1-04-item-transactions`。
每個 patch 測試後交使用者獨立 review，處理 findings 才進下一個；每個 patch 一個 commit，不自行合併或推送。

## 開工前固定的語意

- use case 輸入為已驗證身分所對應的 `roomId`、`participantId`；S2 憑證驗證器負責取得身分，不讓 HTTP body 指定 Owner。S1-04 仍於鎖內查詢 Participant 同房關係，不能僅靠 FK 拒絕。
- 格式驗證先行；鎖內的檢查順序固定為房間有效性 → Participant 屬於該房 → 操作權限／容量。
- 新增拒絕空白、超過 8,192 UTF-8 bytes、NUL（U+0000）及不成對 surrogate；嚴格 UTF-8 編碼，不取代、不截斷、不 trim 原文。NUL／不合法 Unicode 使用 InvalidUnicode 應用錯誤，S2 對應 400。
- 開短交易、`SET LOCAL lock_timeout = '2s'`、`SELECT ... FOR UPDATE` 鎖 Room，完成後另發 `SELECT clock_timestamp()`。不可用 `now()`，也不把時鐘放在等鎖的 SELECT list。2 秒是候選值，S8 再量測；鎖逾時保留 PostgreSQL 55P03（Npgsql EF 預設策略包在 InvalidOperationException 內），不當成容量／授權失敗、不自動重試，HTTP 映射留 S2。
- 三條路徑統一 Rooms → children 鎖序；文字、統計、Version 在同一交易提交。成功才增加版本；TotalBytes 僅統計，不用來拒絕容量。
- 已失效房間優先回 RoomUnavailable，無論呼叫者是否為房主。再次／併發關房輸的一方回不可用，不作冪等成功，也不再加版本。
- commit 前故障／取消須完整回滾；commit 期間故障／取消結果可能不確定，不能宣稱必然回滾，也不得自動重送。測試以明確 commit 後注入點證明可能已提交，不宣稱模擬真實網路競爭。
- 299 筆與 200×8 KiB 前置資料由正式 AddItem use case 建立，測試不另寫新增商業邏輯。故障／非法資料注入只用測試 interceptor 或明確 fixture；FK 最後防線保留獨立測試。

## Patch 1：新增文字（本次範圍）

- 正式 AddItem／ItemAdditionStore 與 DI；同房查詢、鎖內 300 筆上限、Owner 後端決定。
- 文字插入、ItemCount／TotalBytes／Version、CreatedVersion 原子提交。
- 測試原文／UTF-8／非法 Unicode、299 筆搶最後一格、一般併發、200×8 KiB、權限檢查順序、不存在／已失效房間、鎖逾時、故障／取消及 commit 後例外。
- 真 PostgreSQL、獨立 context／連線與同步點；驗證實際計數、bytes、版本與無部分寫入。
- Build、格式、相關及完整回歸測試後交使用者 review；本次不開始 Patch 2。

## Patch 2：刪除自己的文字

- 鎖內同房／Owner 驗證，房主亦不能刪他人單筆。
- 同房不存在 changed=false；實際刪除才扣計數／bytes 並加版本。
- 測重刪競爭、不同筆刪除、新增／刪除交錯、跨房、取消與回滾；獨立 review。

## Patch 3：關房

- 僅 Creator 可設定 DeletedAt 並加版本；不實體清除、不改保留碼期限。
- 重複／併發關房輸的一方得到 RoomUnavailable。
- 測權限、關房與新增／刪除兩種鎖取得順序、故障回滾；獨立 review。

## 範圍界線

基本期限／DeletedAt 檢查在 S1-04 實作；精確等於期限、等待鎖後到期等完整邊界證據留 S1-05。
HTTP／Credential 驗證器、JSON body 32 KiB、SignalR 提交後通知與 Worker 依既定 S2／S3／S5／S6 處理。

## Patch 1 交付狀態（2026-11-16）

已實作並完成 Build／格式檢查／完整測試（51 passed，含新增 27 例），待使用者獨立 review。
最後一格競爭測試先暫停第一個已寫入但未提交的交易，再由 `pg_blocking_pids` 證實第二連線已等鎖才放行。
取消測試分為預先取消、等待鎖取消、SaveChanges 後／commit 前取消，以及明確 commit 後注入取消；不以後者宣稱真實網路競爭已覆蓋。
既有 Owner FK 正反例持續通過，與新 use case 的同房拒絕測試共同覆蓋兩層防線。
完整指令、失敗紀錄與範圍限制見 [testing.md](testing.md)。
