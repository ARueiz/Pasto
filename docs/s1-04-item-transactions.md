# S1-04：併發交易核心

本階段分為新增、刪除、關房三個 patch，沿用現有 schema／Migration，不新增套件。
從已 pull 的 main `1ad0e2b`（PR #2 合併結果）建立 `feature/s1-04-item-transactions`。
每個 patch 測試後交付獨立 review，處理 findings 並獲使用者確認才進下一個；每個主要 patch 各一個 commit，已核准的 review 增量另行提交，不改寫歷史、不自行合併或推送。

## 開工前固定的語意

- use case 輸入為已驗證身分所對應的 `roomId`、`participantId`；S2 憑證驗證器負責取得身分，不讓 HTTP body 指定 Owner。S1-04 仍於鎖內查詢 Participant 同房關係，不能僅靠 FK 拒絕。
- 格式驗證先行；鎖內的檢查順序固定為房間有效性 → Participant 屬於該房 → 操作權限／容量。
- 新增拒絕空白、超過 8,192 UTF-8 bytes、NUL（U+0000）及不成對 surrogate；嚴格 UTF-8 編碼，不取代、不截斷、不 trim 原文。NUL／不合法 Unicode 使用 InvalidUnicode 應用錯誤，S2 對應 400。
- 開短交易、`SET LOCAL lock_timeout = '2s'`、`SELECT ... FOR UPDATE` 鎖 Room，完成後另發 `SELECT clock_timestamp()`。不可用 `now()`，也不把時鐘放在等鎖的 SELECT list。2 秒是候選值，S8 再量測；Infrastructure 只將取得房間鎖時的 PostgreSQL 55P03（直接或由 InvalidOperationException 包住）轉成 Application 的 RoomBusyException，不當成容量／授權失敗、不自動重試。取消、其他 DB 故障及 commit 例外原樣傳遞；HTTP 503 映射留 S2，API 不需引用 Npgsql。
- 三條路徑統一 Rooms → children 鎖序；文字、統計、Version 在同一交易提交。成功才增加版本；TotalBytes 僅統計，不用來拒絕容量。
- 已失效房間優先回 RoomUnavailable，無論呼叫者是否為房主。再次／併發關房輸的一方回不可用，不作冪等成功，也不再加版本。
- commit 前故障／取消須完整回滾；commit 期間故障／取消結果可能不確定，不能宣稱必然回滾，也不得自動重送。測試以明確 commit 後注入點證明可能已提交，不宣稱模擬真實網路競爭。
- 299 筆與 200×8 KiB 前置資料由正式 AddItem use case 建立，測試不另寫新增商業邏輯。故障／非法資料注入只用測試 interceptor 或明確 fixture；FK 最後防線保留獨立測試。

## Patch 1：新增文字（已交付）

- 正式 AddItem／ItemAdditionStore 與 DI；同房查詢、鎖內 300 筆上限、Owner 後端決定。
- 文字插入、ItemCount／TotalBytes／Version、CreatedVersion 原子提交。
- 測試原文／UTF-8／非法 Unicode、299 筆搶最後一格、一般併發、200×8 KiB、權限檢查順序、不存在／已失效房間、鎖逾時、故障／取消及 commit 後例外。
- 真 PostgreSQL、獨立 context／連線與同步點；驗證實際計數、bytes、版本與無部分寫入。
- Build、格式、相關及完整回歸測試後交付獨立 review；已由 Claude 審查並獲使用者確認進入 Patch 2。

## Patch 2：刪除自己的文字

- F2 最小抽取已完成：Domain RoomRules 定義 8192／300 與有效性判斷；Infrastructure 內部 RoomWriteAccess 共用設逾時 → 鎖 Room → 另取 clock_timestamp → 有效性 → 同房檢查。沿用 RoomBusyException，不建立通用 Repository。歷史 Migration 保持原常數，不引用目前 Domain。
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

初次交付 `6612d06` 已完成 Build／格式檢查／完整測試（51 passed，含新增 27 例）。Review Agent（Claude）本輪僅做獨立靜態審查，P0～P2 為 0、P3 為 3；未獨立重跑測試。審查後 F1 修正的相關測試為 29 passed；Claude 增量靜態審查 No Actionable Findings，使用者已核准提交並開始 Patch 2。
最後一格競爭測試先暫停第一個已寫入但未提交的交易，再由 `pg_blocking_pids` 證實第二連線已等鎖才放行。
取消測試分為預先取消、等待鎖取消、SaveChanges 後／commit 前取消，以及明確 commit 後注入取消；不以後者宣稱真實網路競爭已覆蓋。
既有 Owner FK 正反例持續通過，與新 use case 的同房拒絕測試共同覆蓋兩層防線。
完整指令、失敗紀錄與範圍限制見 [testing.md](testing.md)。

## Review findings 處置

- F1：本輪修正。房間鎖取得逾時轉 RoomBusyException；真 DB 驗證轉換、未重試及無殘留寫入，新增直接／包裝的非 55P03 故障原樣傳遞測試。捕捉範圍不涵蓋 commit，既有取消與 commit 後例外測試持續通過。
- F2：Patch 2 已完成最小抽取；RoomUnavailableException、RoomParticipantMismatchException 已移到 Application.Rooms，與 RoomBusyException 一致。
- F3：記錄為測試穩定性風險，目前未觀察偶發失敗，維持 1.5 秒觀察窗與產品 2 秒鎖逾時。縮短觀察窗只會更早失敗，無法消除 OS 排程延遲；若發生，保留證據再調整同步設計，不以加大產品逾時掩蓋。
- 等鎖後關房測試已在 Patch 3 執行；等鎖後到期測試留 S1-05，Program.cs 的 DI 驗證留 S2，後兩者尚未執行。

## Patch 2 交付狀態（2026-11-17）

- 基準為已核准 F1 增量的 `09d277f`，沿用同一功能分支。新增 DeleteOwnItem／ItemDeletionStore 與 DI，無 schema／Migration／套件變更。
- use case 輸入 roomId／participantId／itemId；先鎖房並驗有效性、同房身分，再限定 RoomId＋itemId 查詢。僅 Owner 可刪，Creator 無例外特權；他房 itemId 視為本房不存在，不讀取或改動他房資料。
- 實際刪除才扣筆數／ContentBytes 並遞增版本，與刪除同交易提交；不存在回 changed=false 與鎖內版本，仍完成短交易，不改統計或版本。成功結果只在 commit 後回傳。
- 24 個新測試包含房主／一般 Owner、拒刪他人、失效／同房優先順序、跨房／不存在、重刪競爭、不同筆併發刪除、新增／刪除兩種鎖順序、釋放滿房容量、鎖逾時、等待鎖取消、提交前回滾及提交後例外。所有文字與容量資料均經正式 AddItem 建立。
- 相關新增／刪除測試 53 passed；完整回歸 77 passed／0 failed／0 skipped，補齊 F1 修正後既有 53 例的完整通過證據。Build 0 warning／0 error、格式與差異檢查通過。
- 待獨立 review；本次尚未執行 Patch 3 關房、S1-05 期限邊界、S2 host DI／HTTP、SignalR 或壓測。
- F3 觀察窗維持原值，目前未遇偶發失敗。55P03 只轉換鎖房查詢；交易後段其他 DB 例外仍原樣傳遞，S2 需一般 DB 故障的 503／500 對應。

## Patch 2 審查結果

Review Agent（Claude）對 `09d277f..c060864` 僅做靜態審查，P0～P2 為 0、P3 為 1；未獨立執行 Build／測試。使用者提供的審查結論允許開始 Patch 3。P3 指出新增／刪除測試的同步與故障注入工具重複，本次已移至 TransactionTestHooks。

## Patch 3 交付狀態（2026-11-17）

- 基準 `c060864`，沿用同一分支；CloseRoom／RoomClosureStore 與 DI 已實作，無 schema／Migration／套件變更。
- 關房沿用 RoomWriteAccess：有效性 → Participant 同房 → Creator；以鎖後 DB 時間設定 DeletedAt、Version 加一，同交易提交。只更新這兩欄，不實體刪除，不更動統計、ExpiresAt 或保留碼期限。
- 重複／併發關房的輸方回 RoomUnavailable；非房主遇已關房間也先回不可用。成功結果只在 commit 後回傳，commit 結果不確定仍不自動重送。
- 新增 22 例（21 個真 DB、1 個預先取消）：基本與跨房權限、失效優先順序、併發關房、關房先取得鎖時新增／刪除／非房主關房被拒絕、寫入先取得鎖則完成後再關房、關房回滾後等待新增成功、逾時／取消與提交前後故障。競爭先觀察 pg_blocking_pids，再放行持鎖交易。
- TransactionTestHooks 原樣集中觀察窗、鎖嘗試／barrier、SaveChanges 與 commit 故障工具；全部狀態仍屬每例建立的 instance。原兩份 AfterSave 在沒有取消來源時是否檢查 token 略有不同，保留兩個型別，避免搬移改變行為；原測試斷言不變。
- 關房相關 22 passed，完整 99 passed／0 failed／0 skipped；Build 0 warning／0 error，格式／差異檢查通過。本 patch 待獨立 review，不宣稱已由 Claude 審查。
- 尚未執行 S1-05 的精確期限／等待鎖後到期邊界、S2 host DI／HTTP、SignalR、Worker 或壓測；2 秒鎖逾時與 1.5 秒觀察窗的 F3 風險保留。
