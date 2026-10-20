# Pasto — Sprint 1 分階段實作計畫

日期：2026-10-20。狀態：**待使用者核准，尚未執行**。本輪只產出文件，不建立專案、程式碼、Migration、容器或套件。

依據：[最終需求](requirements.md)、[最終架構](architecture.md)、[ADR 0002](decisions/0002-room-code-and-anonymous-participants.md)。ADR 0001 僅供歷史比較，不是實作來源。

## 1. 執行原則與順序

以小範圍、可驗證的變更逐步完成 MVP。每階段可分成數個獨立 Review 的小 patch；先執行與本次變更相關的 Build／Lint／Tests，記錄實際結果，不在全部完成前稱 MVP 已完成。

順序：**S1 DB 正確性 → S2 房間／匿名授權 → S3 文字／分頁 → S4 基本 UI → S5 方案 A 即時同步 → S6 清理／冷卻 → S7 防護／資源 → S8 壓測與收尾**。

S1 是資料庫設計的驗證關卡，不能跳過後直接生成完整應用。S2 開始已有基本輸入、Credential 脫敏與公開限流；S7 是完整安全／資源驗證，不代表前幾階段刻意不做基本保護。尚未完成防護的階段只在隔離本機環境驗證，不發布公開站台。

本計畫獲准後才建立最小必要專案、依賴與環境；不預先建立大量空專案。後續若需要改已核定不變條件或增加 MVP 外需求，先提出影響與修訂，不自行擴張。

## 2. S1：PostgreSQL 資料模型與交易整合測試

目的：用真 DB 證據排除循環 FK、EF 寫入順序、清理與併發風險，先證明不變條件再做 HTTP／UI。

| 小工作 | 交付 | 驗收條件 |
| --- | --- | --- |
| S1-01 最小完整性測試骨架 | 最小 .NET 10 測試／持久層範圍、固定 PostgreSQL／EF provider 版本的候選清單、可拋棄測試 DB／schema、四表測試 DDL／mapping、執行說明 | 真 PostgreSQL 可重跑；不是 InMemory／SQLite；不連共享／正式 DB，不建立完整 API／前端；測試失敗確實使指令失敗 |
| S1-02 Creator／Owner 約束 | 複合 FK、DEFERRABLE／NO ACTION 與 cascade 的整合測試；比較分階段 EF 與窄範圍參數化 SQL 的建房交易 | 同房有效建立 commit；跨房 Owner／Creator、缺房主及中途失敗均不留下部分資料；循環建立可用 EF／同一 DbTransaction 實際完成，不只手寫 SQL 成功 |
| S1-03 併發交易核心 | 後續可重用的最小新增／刪除／關房交易路徑與整合測試，分成新增、刪除、關房三個小 patch | 299 筆併發新增僅一個成功；刪同筆只扣一次；Owner 權限正確；關房／新增／刪除同鎖序，無負計數／額外 Version，TotalBytes 等於實際統計且不作容量拒絕 |
| S1-04 期限／清理關聯 | 到期邊界、等待鎖後到期、Room cascade 與保留碼回收的測試，附 schema 選擇結論 | 過期／DeletedAt 在鎖後重驗；刪 Room 清 Participant／items，保留碼仍在；冷卻前／仍有 Room 引用不能回收；原子 rollback、FK deferred commit／違約失敗均符合預期 |

第一個可交付 patch 只做 S1-01 與最基本正反例，不一次加入所有交易或 UI。之後擴充實際可重用的交易核心，不在測試內另寫一套永遠不會被應用使用的商業實作。

測試設計要求：

- 至少兩個獨立 DbContext／DB 連線交錯交易，以 barrier／明確同步點建立競爭；不只測循序呼叫，也不靠任意長 sleep 猜時序。
- 注入在 Room 插入後、Participant 插入後、commit 前失敗，檢查 Room／Participant／Code reservation 都回滾；跨房錯誤不得被只在測試 fixture 中關閉的 FK 掩蓋。
- EF tracked graph 可能先判循環；測實際選定寫入路徑、tracking 狀態及 DbTransaction，共用 transactional connection，不只測 DB 支援 DEFERRABLE。
- 測到期前／檢查點等於或晚於 ExpiresAt／已關房，以及持鎖等待後再檢查。以可控制 fixture 時間與 DB 當下時間驗證，避免只用 Client 時鐘或交易起始時間。
- 200 筆×8 KiB 超過舊 1.5 MiB 必須可成立，300／301 筆與單筆 byte 邊界分別驗證；沒有每房總量上限的測試設定。
- 第一階段用隔離測試 schema 驗證，不先產正式 Migration；選定落實方式後，S2 再整理可審閱的正式 schema／Migration。

必要依賴以 EF Core／PostgreSQL provider、測試 runner 為限；xUnit 是候選 runner（用途是可重跑整合測試；MSTest 亦可，先確認版本相容性），DB 可用既定 Docker Compose 啟動，不為此先加 Testcontainers。精確版本與最小清單在獲准後的 S1-01 小變更中列清楚，本輪不安裝。

**S1 出口條件**：上述不變條件有真 DB 的通過／失敗證據，記錄採用 FK／EF 寫入方式與指令。若循環成本過高，先提出同樣保證同房／必有房主／原子性／無孤兒的替代，不以 IsCreator 唯一索引或應用檢查單獨降級 DB 保證；未解決前不展開依賴此模型的大量 API。

## 3. S2：房間、匿名 Participant 與授權 REST

交付以小 use case 拆分：建房與房主 Credential → Join／恢復 → 房間狀態與房主關房。建立最小 API host、DI／設定／ProblemDetails、正式 schema／Migration 與本機 DB Compose；Migration 必須沿用 S1 驗證模型，不含檔案表。

同時具備 CSPRNG 六碼／唯一碰撞有界重試、Credential hash、sessionStorage 對應契約、5 小時／DeletedAt 驗證；初始公開限流、可信代理設定、no-store／日誌脫敏與有限 DB pool 先到位。沿用 S1 交易核心，不另寫第二套邏輯。

驗收：

- AC-01～AC-03 的建房／加入／跨房權限部分、AC-10 的即時失效，以及 AC-15 的不確定 POST 錯誤語意可用 HTTP 整合測試驗證。
- 建房回應／QR 用的 URL 不帶 Credential；/me 恢復不新增身分；無效 Credential 不取得他房身分。
- 一般人不能關房，房主關房後讀寫／加入拒絕；DB／通知暫時未接完整時不顯示假成功。
- 正式 schema 在隔離 DB 可建立，Migration SQL 與 S1 不變條件一致；本階段才建立／在隔離環境驗證 Migration，不觸碰使用者既有或正式資料庫。

## 4. S3：文字 CRUD、容量及 Keyset Pagination

交付分三段：新增／刪自己的文字 → metadata → 版本綁定 Keyset。新增 8 KiB 與 JSON Body 32 KiB 分別驗證、300 筆鎖內檢查、TotalBytes 只統計；API 不接受 Owner／Creator 欄位。游標綁 RoomId／snapshotVersion／排序鍵／limit 並簽章，不加通用分頁框架或單筆同步 API。

驗收：

- AC-03～AC-07：UTF-8 多位元／空白／換行保留，8,192／8,193 bytes、32 KiB body 邊界、超舊總量可新增、第 301 筆拒絕，併發計數／Version 正確。
- 預設 50、最多 100；無重複／跳頁排序錯誤；byte 上限可能少回但 nextCursor 正確、不截斷文字。原文合法但 JSON 過大時明確 413。
- 竄改／他房游標無法跨房；兩頁中間新增或刪除回 SnapshotChanged，不假回一致列表；查詢計畫使用房間／排序索引，查詢有界。
- Owner 授權在後端，房主也不能刪他人單筆；同房不存在為 changed=false，不另扣計數或加 Version。

## 5. S4：最小前端與身分／分享操作

交付 React + TypeScript + Vite 基礎頁面：建房、輸入碼／URL 加入、Client QR、sessionStorage 恢復與主動離開、文字輸入／byte 提示／列表／載入更多／複製／刪自己／房主關房。分成「身分分享」及「文字操作」兩個小 patch；尚未完成 SignalR 前以明確手動刷新驗證，不宣稱已即時。

驗收：

- AC-02／AC-03／AC-04／AC-11：手機 QR 新身分、F5 原身分、離開清本分頁、複製原文、8 KiB byte 提示、同房所有權 UI，後端仍是授權依據。
- QR 只在 Client 編碼加入 URL，不依賴外部產碼服務；按需求列最小 QR 依賴／替代方案，不加檔案功能。
- Clipboard API 拒絕有手動選取方式；HTML 只顯示文字，無 dangerouslySetInnerHTML；sessionStorage 失效／無法保存時有明確身分狀態，不謊稱可恢復。
- 前端 Build／TypeScript／Lint 與必要 UI 測試實際執行；說明分頁複製／Session Restore 可能保留副本。

## 6. S5：SignalR 與方案 A 同步

交付 Server RoomChanged／授權訂閱，再交付 Client 合併提示、單一 round、同版本快照組裝、requestedCount／部分載入、generation／AbortController、重連與前景／15～20 秒 jitter 檢查。分成「授權與提交後提示」及「Client 同步」小 patch，不建立事件缺號緩衝、單筆 GET、局部刪除狀態機、Outbox 或重放框架。

驗收：

- AC-08／AC-09：先訂閱再讀；連續大量重複／亂序通知只安排有界工作；提示版本非連續也正常重建，Server 不假裝保證交付。
- 兩瀏覽器新增／刪除後 REST 收斂，通知只含 Room.Id／version；其他房無資料／提示。commit 失敗無提示，commit 後通知失敗資料仍可讀。
- 慢 GET、切房、離開、401、關房後晚回應不能覆蓋新狀態，舊房晚到的 401／404 也不能清新房；所有暫存頁同版本才替換；SnapshotChanged 有限重試、無忙迴圈，持續寫入時明確同步中、安靜後收斂。
- 只要求 50 筆不抓滿 300；載入更多重建新目標範圍；部分載入不宣稱完整。重連重驗／重新訂閱；回前景檢查。
- 故意丟最後一次通知與模擬通知前程序中斷，記錄健康前景修復延遲；先做功能證據，最終 1,000 人時間目標在 S8 驗證。

## 7. S6：Worker、冷卻回收與生命週期

交付每分鐘清理、每批最多 100 房、同鎖序與 SKIP LOCKED、每輪時間預算、不重疊／可補清；RoomCodeReservations 獨立回收；有界到期／關房連線清理。拆成「主資料」及「保留碼／連線」兩小 patch。

驗收：

- AC-10～AC-12：Worker 停止不恢復過期存取；主資料 cascade 全清、保留碼仍在；重跑安全、停機恢復可補清。
- 提前關房不縮短原 ExpiresAt + 24 小時；回收需無 Room 引用；碰撞／回收競爭不重配有效碼；舊 Credential 無新 UUID 權限。
- 造超過 100 房的 backlog，驗證分批、公平選失效時間及每輪時間預算；量測健康情況失效後 10 分鐘目標，超時有 backlog／最久失效指標。
- 到期／關房 group 清除可能遺漏時，REST 與後續 Hub 驗證仍拒絕，Client 校正清 UI；不保留歷史查詢／還原。

## 8. S7：安全、資源與監控整合

交付需求第 5 節的可設定候選值、公開錯碼退避／全站壓力模式、已驗證配額、Hub 方法限制、分類 HTTP 並行、pool／連線／部署限制、bounded partition 與恢復遲滯。敏感資訊脫敏／CSP／Origin／代理驗證及指標分開 Review。

驗收：

- AC-13／AC-15：多來源猜碼、連續錯碼、NAT 合法使用者、偽造 X-Forwarded-For、無效 Credential 及直接 Hub invocation 有預期防護，429／Retry-After、503 明確。
- partition／失敗記錄達上限仍有界，新鍵不直接放行；通知／Client dirty／伺服器連線追蹤不因輸入數量無界成長。
- 高負載先限制建／加入，讀、刪、清理仍有預算；全站連線足夠容納目標千人，同 IP 不被過小連線額度卡住；DB pool 合計與 max_connections 有餘裕。
- 不引入全站 DB 配額列，不用 Room.TotalBytes 拒絕總量；監控資源與查 SQL／鎖等待，確認每次寫入不爭用額外全站列。
- 日誌、代理／APM／SignalR access_token 全鏈路檢查無正文／明文 Credential；CORS 不替代 WebSocket Origin／同房授權。

## 9. S8：負載證據、驗收與文件收尾

交付可重跑負載情境／版本與硬體設定、結果報告、查詢計畫／主要瓶頸、最終候選設定調整與已知限制；補齊操作／本機啟動及測試說明。使用既定單實例，不因測試數字就新增 Redis 等基礎設施。

驗收情境：

| 情境 | 必須記錄／驗證 |
| --- | --- |
| 1,000 連線／約 200 房／典型 5 人 | 公開建房／加入暖機及升載過程、metadata 約 50～67 req/s、實際寫入＋所有快照補償；不能忽略衍生讀取 |
| 同一 NAT／公司／學校出口 | 既有千條連線與新加入速率分開測；合法成功率／429，不能只用不同 IP 模擬就聲稱支援 NAT |
| 熱房／最大文字 | 高集中度多人新增刪除、300 筆×8 KiB、所有人已載入多頁；觀察快照衝突／重抓、UI 收斂及網路用量 |
| 重連／通知遺漏 | 大量同時重連 jitter、全部或部分提示遺漏、回前景補償；修復主目標 20～30 秒、健康情況約 60 秒容許上限 |
| 濫用／資源／清理 | 分散錯碼、partition 耗盡、pool／CPU／記憶體／磁碟壓力、過期 backlog 與清理保留資源 |

候選基準為 app／DB 各 4 vCPU／8 GiB、每房初始 50 筆／平均 1 KiB、全站每秒 20 次平衡新增／刪除、30 分鐘，再做階梯與邊界；REST p95 ≤ 500 ms、提交到畫面 p95 ≤ 2 秒仍待量測核定。測試報告一併列合法成功率、429／5xx、實際接收通知／同步量、修復分布、DB lock／pool 等待及資源，不用拒絕正常流量粉飾達標。

S8 出口：需求 AC-01～AC-15 有可追溯證據。若未達 1,000 或時間目標，如實標示未達與瓶頸／調整提案，不標成已支援；涉及架構改動另 Review。完整 Build／Lint／相關 Tests 通過後，無新修改或疑慮不重複擴大測試。

## 10. 建議第一個實作任務與完成回報

**建議第一個任務：S1-01「建立最小 PostgreSQL／EF Core 完整性測試骨架」**，接著只驗證一個同房建房／房主成功 commit 與一個跨房 Creator 失敗反例。先取得真 DB 與 EF 寫入證據，再增 Owner／cascade／併發案例；不從完整前後端專案生成開始。

每個小 patch 回報：改哪些檔／為何、實際測試指令與結果、尚未驗證項目、已知限制與下一步。只有計畫獲准才開始上述工作，本輪無任何階段已完成的宣稱。

Phase 2 Object Storage／Metadata／檔案權限／可重試清理僅留在架構方向，不是本計畫交付，不建立 RoomFiles、IFileStorage 或檔案 API。
