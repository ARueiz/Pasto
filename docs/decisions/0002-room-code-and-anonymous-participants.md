# ADR 0002：六碼房碼、房間內匿名參與者與文字所有權

- 狀態：Accepted（2026-10-20 最終 Review：Q1～Q7、T1～T8 方向已確認；限流／效能候選值及資料庫落實方式須實測）
- 日期：2026-10-20
- 取代：[ADR 0001 的未核定提案](0001-initial-architecture.md)，其狀態為 Withdrawn，保留歷史
- 現行規格：[requirements.md](../requirements.md)、[architecture.md](../architecture.md)
- 本輪範圍：文件更新，未實作、未安裝套件、未壓測
- 後續：[Sprint 1 實作計畫](../sprint-1-implementation-plan.md)
- 部分修訂：[ADR 0003](0003-mvp-scope-reduction.md)（2026-11-05）將第 9 項的 Keyset 分頁改為 MVP 整份快照、第 12 項的同步改為重抓整份快照，並將分層限流收斂為 MVP 基本限流；下文其餘決策不變。下文提及分頁、游標、SnapshotChanged 或 requestedCount 之處，MVP 以 ADR 0003 為準

## 背景與歷史處理

Pasto 主要用於會議室、遠端電腦／跳板機及手機間的臨時文字交換，需要易輸入的房碼與 QR Code，也要區分匿名使用者的文字刪除權。原提案把高熵 Room Token 同時用作識別與共同權限，不能直接滿足「只能刪自己的文字」及「房主提前關房」。

ADR 0001 當時是 Proposed，並非已接受的決策；此次保留原內容，僅更新狀態及歷史說明，再以本 ADR 記錄正式方向。如此既不覆寫提案沿革，也不誤稱舊方案曾獲批准。現行需求及架構引用本 ADR；舊文件中的待確認問題與數值不再當作現行規格。

## 最終 Review 修訂紀錄

本 ADR 直接補充同一輪 Sprint 1 的最終決策，以下現行段落同步修正，避免把未採用的技術提案當現行規格；ADR 0001 不再修改。這次選方案 A 只簡化同步，沒有恢復舊共同 Room Token 或人人可刪的授權模型。

| 先前提案／未決值（歷史） | 最終決策 |
| --- | --- |
| 單筆 4 KiB、每房 1.5 MiB | 單筆 8 KiB、300 筆；不另設每房文字總量硬上限，TotalBytes 僅統計 |
| JSON／分頁尚待確認 | Body 32 KiB，Keyset 預設 50、最多 100；原文與 Body 獨立驗證；回應 byte 預算仍需實測 |
| 加入每 IP 3,000 次／分鐘 | 撤回；公開猜碼／錯碼退避／全站壓力、已驗證 REST、每房、Hub 與全站資源分層，數字待安全測試／壓測 |
| 全站原子資料庫配額計數器列為選項 | MVP 不採；以 HTTP 並行、DB pool、連線、部署硬限制／警戒與 admission 保護 |
| 清理週期與冷卻待定 | 每分鐘、每批最多 100 房、健康時 10 分鐘內清主資料；獨立 PostgreSQL 保留碼至少至原 ExpiresAt + 24 小時 |
| 逐筆取得／刪除、缺號緩衝與連續版本狀態機 | 第一版正式採方案 A：合併 RoomChanged 提示，REST 重建所需同版本分頁；不重放／逐筆套事件 |
| FK／交易僅列後續驗證 | 第一階段先做真 PostgreSQL Integration Tests，驗證循環 FK／EF 排序、deferred、原子建立、同房、cascade、併發與期限 |

Phase 2 僅記錄 Object Storage＋Metadata、既有所有權／到期、獨立檔案配額與可重試清理，不建檔案模型／API／套件。

## 已確認的決策

1. 保留 React + TypeScript + Vite、ASP.NET Core .NET 10、PostgreSQL + EF Core、SignalR、Docker Compose 與四層 Modular Monolith。單一 Application + PostgreSQL 起步，REST 操作資料、commit 後通知，DB 是 Source of Truth。
2. Room.Id 使用內部 UUID；Room.Code 使用 CSPRNG 固定六碼 Base36（A–Z、0–9），DB UNIQUE，碰撞重新產生並有界重試。不使用動態前綴或截短高熵 Token。
3. Code 用於手動加入、分享 URL 與 QR Code；QR 僅包含加入 URL。任何知道有效 Code 的人都可以加入，不能將 Code 說成高熵安全憑證。
4. 新增 RoomParticipants：Id、RoomId、CredentialHash、CreatedAt。後端產生至少 128-bit 隨機 Credential，DB 只存 hash；REST 與 Hub 驗證身分、所屬房間、期限及操作權。
5. ClipboardItems 記錄 OwnerParticipantId，Rooms 記錄 CreatorParticipantId；Owner／Creator 必須同房。Client 不能指定這些授權欄位。
6. 所有人可讀、新增、複製；只能刪自己的文字。房主不能單筆刪別人的文字，但可提前刪整個房間；不提供權限轉移、共同管理員或身分找回。
7. 每房 Credential 用 sessionStorage 保存，F5 恢復原身分；提供「離開並清除此分頁身分」。不用 localStorage 長存，不依賴 beforeunload 刪後端身分，不宣稱關頁立即銷毀。新手機掃碼不繼承原裝置權限。
8. 固定建立後 5 小時失效，不因活動延長；不支援延長／復原。房主提前關房後同樣拒絕後續讀寫／訂閱，已連線者接通知，遺漏則靠 REST／重連發現。
9. 單筆原文最多 8 KiB（8,192 UTF-8 bytes）、每房最多 300 筆；保留原文並拒絕全空白、超限拒絕而非截斷。不另設每房文字總量硬上限，TotalBytes 僅觀測；原單筆 4 KiB／每房 1.5 MiB 已取代。新增 Body 32 KiB 與原文獨立驗證；Keyset 預設 50、最多 100 筆。
10. 不做產品層備份、歷史查詢或還原；邏輯失效與背景實體刪除分開。清理涵蓋 Participants 與 items，WAL／基礎設施快照／日誌另有生命週期。
11. 目標測試 1,000 同時在線 SignalR 連線，典型每房 5 人、約 200 房；5 不是硬上限。熱門房、同 NAT、併發及重連／REST 補償都納入測試，尚未證明達標。
12. 正常接近即時，遺漏修復主目標 20～30 秒，最長約 1 分鐘可接受。前景每 15～20 秒加 jitter 查版本、重連／回前景檢查；方案 A 合併變更提示後重新取得目前所需範圍，不為每則提示各重抓，也不逐筆套用。修復／讀取成本須量測。
13. Worker 每分鐘清理，每批最多 100 房，可安全重跑／補清，健康時以失效後 10 分鐘內清主資料為目標；房碼獨立保留至少至原 ExpiresAt + 24 小時，提前關房不縮短。
14. PostgreSQL transaction、Rooms 單列 Row Lock、鎖後重驗有效性、DeletedAt、Owner／Creator 後端驗證，計數／Version 同交易；commit 後通知，通知失敗不回滾，不確定 POST 不自動重送。第一階段先驗證必要 DB 不變條件。

## 原始與新版差異

| 主題 | ADR 0001 原提案（歷史） | 本輪方向／影響 |
| --- | --- | --- |
| 房間識別與加入 | 128-bit Room Token 兼共同 capability | UUID Room.Id＋六碼 Code；易手輸入，但猜中風險增加 |
| 匿名權限 | 沒有 Participant，持有連結同權且可刪任意筆 | 高熵 Participant Credential＋所有權；只刪自己，房主可關整房 |
| 分享與跨裝置 | 長 Token URL | 六碼輸入＋URL／QR；只帶 Code，不複製身分 |
| 前端保存 | 房間 token 主要來自 URL，無獨立身分生命週期 | sessionStorage 保存每房 Credential；支援 F5，但須處理殘留／遺失 |
| 模型與授權 | Rooms、ClipboardItems | 增加 RoomParticipants、Owner／Creator 關聯、同房約束；需處理循環 FK |
| 房間生命週期 | 24 小時建議，未含手動關房 | 固定 5 小時＋房主關房已確認；清理與邏輯失效分離 |
| 容量 | 100 筆／16 KiB／1 MiB 建議 | 單筆 8 KiB、300 筆，無額外每房文字總量硬上限；TotalBytes 僅統計 |
| 規模與限流 | 500 連線基準、低 IP 額度 | 1,000 目標；NAT 友善的寬鬆 IP 粗限制＋Participant／房間保護，精確數值待壓測 |
| 同步 | 所有事件使快照失效並重抓；25～35 秒檢查／40 秒修復提案 | 方案 A：提示合併、重取目前需要的同版本頁面；15～20 秒檢查、20～30 秒主修復目標；不恢復舊授權模型 |
| 房碼重用 | 高熵 token 不以人工短碼為核心，未設冷卻 | 獨立 PostgreSQL 碼紀錄至少保留至原 ExpiresAt + 24 小時；舊身分始終綁原 UUID |

## 取捨與替代方案

### 便利性與保密性

六碼共 36^6 = 2,176,782,336 種，約 31 bits，遠低於原本 128-bit Room Token。接受此風險是為了方便手動輸入與 QR 跨裝置使用。Credential 的高熵保護身分與刪除權，不能阻擋猜中房碼後讀取內容。必須限制建房／加入、不提供列表、HTTPS、脫敏日誌及機密資料提醒；限流不保證分散式猜碼不會成功。

保留長 Token、加密碼或登入審核可提高加入保護，但改變已確認的快速匿名體驗，不納入 MVP。將識別／加入與匿名授權分開有利後續擴充，並不表示本次加入會員功能。

### 所有權與身分保存

Room-scoped Participant 能限制刪除、隔離各房身分，也讓房主權明確；代價是多一張表、授權與資料清理成本。sessionStorage 減少刻意跨瀏覽器工作階段長存，且可 F5 恢復，但可能被分頁複製／還原、共用設備殘留或 XSS 讀取。清除此分頁不等於撤銷其他副本，Credential 遺失無法恢復原權限。[sessionStorage](https://developer.mozilla.org/en-US/docs/Web/API/Window/sessionStorage)

HttpOnly cookie 可降低腳本直接讀取的風險，但改變分頁隔離與 CSRF／跨房管理；localStorage 方便長期恢復但增加殘留；帳號可找回身分但超出 MVP。維持已確認的 sessionStorage，不擅自新增恢復碼或權限轉移。

### 同房關聯與交易

技術提案是複合 FK 保證 Owner／Creator 同房，反向 Creator FK 採 DEFERRABLE INITIALLY DEFERRED，以一個交易原子建立 Rooms 與房主，清理時由 RoomId cascade 刪 children，反向關係延後檢查。這比只在應用檢查多一層 DB 保證，但需要處理 EF Core 的循環寫入排序，尚未驗證。[PostgreSQL constraints](https://www.postgresql.org/docs/current/ddl-constraints.html)、[Deferred checks](https://www.postgresql.org/docs/current/sql-set-constraints.html)

替代方案 Participant.IsCreator＋唯一部分索引可避開循環，但只能直接保障「最多一位」，仍需額外保證建立時必有房主；目前優先保留明確 CreatorParticipantId。所有單房異動維持短列鎖，Owner、容量、Version 與關房狀態同交易，不加分散式鎖。

### 方案 A 與遺漏修復

第一版採 RoomChanged（roomId、version）失效提示；Client 只保存最高提示版本、dirty 與一個進行中的同步 round。短時間提示合併，REST 從第一頁重建目前 requestedCount 範圍，暫存頁面必須同版本才替換。跨頁版本變動則丟棄該 round 並有限重試；AbortController 加 Room.Id／generation／request 序號避免舊回應及跨房污染。部分載入不能宣稱完整。此為在原分頁協定上移除逐筆事件處理的最小修訂。

相較上一輪未採用的單筆 GET／局部刪除方案，方案 A 的 Client 更簡單、容易驗證，但變更時可能重讀多頁，熱房有快照重試與讀取放大。先以合併、byte 預算、限制範圍與壓測驗證；需要時再另立 ADR 比較局部更新。第一版沒有事件缺號緩衝、持久重放、Outbox／Message Queue 或 exactly-once。SignalR group 不是授權，重連仍須驗證／訂閱；通知不是可靠佇列。[SignalR groups](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups?view=aspnetcore-10.0)

### 冷卻與刪除

採輕量 PostgreSQL RoomCodeReservations，與建房同交易取得碼；至少至原 ExpiresAt 後 24 小時、且無 Rooms 引用才可回收，提前關房不縮短。與保留整房資料相比，分離保留碼避免為了冷卻延長文字／身分保留；與程序內集合相比可跨重啟並由 DB 唯一約束仲裁，不需全域分散式 ID。回收、碰撞重試與 cascade 行為由整合測試驗證。

冷卻不是永久保證，舊 URL／QR 之後仍可能指向新房；舊 Credential 必須綁 UUID，失效後不可靜默改綁。無限保留所有碼可消除重配，但會持續耗用有限命名空間與資料，故不建議在 MVP 採用。

房主關房先標 DeletedAt 以持久化立即失效，後續 Worker 清除資料；相較同步 cascade 所有資料，回應時間較可控，但物理移除有延遲。每分鐘、每批最多 100 房，健康時 10 分鐘內清主資料是目標，需觀測 backlog。DeletedAt 不提供歷史或復原。MVP 不做產品備份，仍須誠實記錄基礎設施副本可能存在。

## 實測範圍與實作核准

Q1～Q7 與 T1～T8 的方向不再待定；仍需測量調整的候選值集中於需求第 5 節：公開猜碼／失敗退避／全站壓力、Participant／房間配額、Hub 與連線預算、HTTP 並行、DB pool、部署資源及回應 byte 預算。MVP 明確不設每次寫入競爭的全站 DB 配額計數器，以有界資源、警戒與 admission 保護，優先限制新建房與加入，保留讀、刪及清理。單實例設定不代表多副本全域一致，也不宣稱完全防分散式猜碼。

第一階段用真 PostgreSQL 測 Creator 原子建立、循環 FK 與 EF 排序／DEFERRABLE、跨房複合 FK、cascade、299 筆競爭、刪除／關房與到期邊界，再建立 API／UI。資料庫必須保障的不變條件不放寬；若目前落實方式不合理，先提出等價替代與測試證據。之後測瀏覽器方案 A、1,000 人、同 NAT、熱房及重連流量，不把目標寫進文件就算完成。

完成此次文件與分階段計畫後停止，等待使用者核准實作計畫；不建立專案、程式碼或 Migration。
