# Pasto Sprint 1 — 最終需求規格

更新：2026-10-20（最終 Review）；2026-11-05 依 [ADR 0003](decisions/0003-mvp-scope-reduction.md) 收斂 MVP 範圍（整份快照取代分頁、基本限流）。Q1～Q7 與 T1～T8 的方向已確認；限流、資源與效能的候選值仍須安全測試／壓測。已建立正式持久層與 Application 建房流程，尚無產品 API／UI；進度見實作計畫。

現行依據：[architecture.md](architecture.md)、[ADR 0002](decisions/0002-room-code-and-anonymous-participants.md)、[ADR 0003](decisions/0003-mvp-scope-reduction.md)。[ADR 0001](decisions/0001-initial-architecture.md) 只保留歷史。執行順序見 [Sprint 1 實作計畫](sprint-1-implementation-plan.md)。

## 1. 產品定位與 MVP 範圍

Pasto 是臨時跨裝置文字交換工具，適用於會議室電腦、遠端電腦／跳板機、手機間交換文字與小型討論。不需會員註冊或登入，不以長期保存為目標。

MVP 包含匿名建房、六碼手動加入、分享 URL／QR Code、多筆純文字、新增、複製、刪自己的文字、房主關房、SignalR 通知、REST 同步、到期清理與資源保護。

MVP 不含檔案／圖片上傳、AI Agent、文字編輯、歷史復原、期限延長、房碼更換、身分／房主權限轉移、共同管理員、公開房間列表、離線寫入或自動讀取剪貼簿。QR Code 僅是加入工具。

技術維持 React + TypeScript + Vite、ASP.NET Core .NET 10、SignalR、PostgreSQL + EF Core、Docker Compose、API／Application／Domain／Infrastructure 四層 Modular Monolith。單一 Application + PostgreSQL 起步，不引入 Microservices、Redis、Message Queue、Distributed Lock 或 Kubernetes。

## 2. Q1～Q7 最終摘要

| 編號 | 決策 |
| --- | --- |
| Q1 匿名與權限 | Room-scoped Anonymous Participant；所有人可讀、新增、複製，只能刪自己的文字。房主不能單筆刪別人的文字，但可關整房。後端驗證 Owner／Creator，同房關聯由 DB 保證。 |
| Q2 期限 | 建立後固定 5 小時，不隨活動延長；到期／提前關房即拒絕後續讀、寫與訂閱，無恢復功能。 |
| Q3 文字 | 每房最多 300 筆，只新增／刪除，保留原文、空白與換行；拒絕空白及純空白。單筆上限依 T1，無額外每房文字總量硬上限。 |
| Q4 保留 | 邏輯失效與分批實體刪除分開；清 Rooms、RoomParticipants、ClipboardItems。不提供產品層備份／還原或歷史查詢；DELETE 不等於安全抹除。 |
| Q5 規模 | 目標 1,000 同時在線使用者／SignalR 連線；約 200 房、每房 5 人是典型測試情境，5 人不是硬上限。必須實際壓測，尚未證明達標。 |
| Q6 同步 | 正常接近即時；遺漏修復主目標 20～30 秒，健康前景情境下最長約 1 分鐘可接受。採 T7 方案 A；背景節流／離線不保證此時限。 |
| Q7 識別 | Room.Id 為內部 UUID；Code 為 CSPRNG 固定六碼 A–Z／0–9，DB 唯一、有界碰撞重試。Code 用於手輸、URL、QR；至少 128-bit Participant Credential 驗證身分，僅存 hash，絕不置於分享 URL／QR。 |

### 權限與身分生命週期

| 操作 | 房主 | 一般參與者 |
| --- | --- | --- |
| 讀／新增／複製文字 | 允許 | 允許 |
| 刪自己的文字 | 允許 | 允許 |
| 刪別人的單筆文字 | 禁止 | 禁止 |
| 刪整個房間 | 允許 | 禁止 |
| 轉移房主／恢復遺失身分 | 不支援 | 不支援 |

每房 Credential 用 sessionStorage 保存；F5 先驗證原憑證，不重新 Join。「離開並清除此分頁身分」只適用於非房主，會清除本機內容、憑證與連線，不撤銷其他副本或刪除文字。MVP 沒有房主權限轉移，房主清除分頁身分後會永久失去關房權限。不用 localStorage 儲存憑證，不依賴 beforeunload 刪 Participant。憑證遺失即失去舊刪除權／房主權，重加是新身分。

分頁複製、opener、Session Restore 與 XSS 可能複製／取得 sessionStorage，不能保證關頁立即銷毀。手機掃 QR 是獨立 Participant，不繼承電腦身分。[sessionStorage](https://developer.mozilla.org/en-US/docs/Web/API/Window/sessionStorage)

六碼只有 36^6 = 2,176,782,336 種（約 31 bits）。知道／猜中即可加入並讀取，Participant Credential 不能阻止猜房碼。無公開列表、HTTPS、限流與提醒均須落實；UI 提醒不要傳密碼、API Key、Token 或機密資料。

## 3. T1～T8 最終決策

| 編號 | 確認內容 | 仍須驗證的範圍 |
| --- | --- | --- |
| T1 文字 | 原文最多 8 KiB = 8,192 UTF-8 bytes；每房最多 300 筆；無額外每房文字總量硬上限；超限拒絕、不截斷；前後端提示、後端為準 | 多位元中文字、emoji、空白、換行及邊界；TotalBytes 只作統計，不作 1.5 MiB 拒絕條件 |
| T2 HTTP／列表 | 新增 JSON Body 最大 32 KiB；MVP 列表一次回傳該房全部文字（最多 300 筆）與 snapshotVersion，CreatedVersion DESC、Id DESC；有效 Room＋Participant（ADR 0003） | 滿房回應大小與 1,000 連線同時重抓的讀取放大；超出目標時再以新 ADR 恢復 Keyset 分頁 |
| T3 分層限流 | MVP：公開建房／加入的每 IP 與全站限流、已驗證新增／刪除限制、每房 Participant 上限、可信代理；其餘分層防護於 S7 依實測補齊（ADR 0003） | 所有配額待安全測試與壓測調整；NAT、連續錯碼、分散來源、partition 有界；補齊前不發布公開站台 |
| T4 全域資源 | 單一 Application＋PostgreSQL；並行請求、DB pool、連線、部署 CPU／記憶體／磁碟限制與監控；過載優先限制新建房及新加入 | 不做每次寫入競爭的全站 DB 配額計數器；單實例不代表多副本全域一致 |
| T5 清理 | Worker 每分鐘一次、每批最多 100 房；安全重跑／補清；健康運作以失效後 10 分鐘內清主要資料為目標 | 每輪可有多批且受時間預算約束；交易時間、backlog、停機恢復、cascade |
| T6 冷卻 | 獨立 PostgreSQL 保留碼；至少至原 ExpiresAt + 24 小時，提前關房不縮短；主資料先刪、保留碼獨立 | 唯一衝突／回收競爭／重試上限；之後可重用，不承諾永久唯一；舊 Credential 始終綁舊 UUID |
| T7 同步 A | 先訂閱再讀；通知只表示變更，短時間合併、單一進行中的同步；版本不同就重取整份快照；重連／前景恢復／15～20 秒 jitter 檢查 | 請求取消／序號、舊回應保護與失效 UI；不建事件缺號緩衝、逐筆套用狀態機、事件重放、Outbox 或 exactly-once |
| T8 交易 | PostgreSQL transaction＋Rooms 單列 Row Lock；文字、計數、Version 同交易；Owner／Creator 後端驗證；DeletedAt；鎖後重驗期限／關房；commit 後通知 | 第一階段真 PostgreSQL 整合測試驗證循環 FK、EF 寫入順序、DEFERRABLE、原子建立、複合 FK、cascade、併發與期限邊界；POST 不確定不自動重送 |

8 KiB 是 bytes，不是固定中文字數。原單筆 4 KiB／每房 1.5 MiB 已取代。300 × 8,192 = 2,457,600 bytes 是既有規則推導出的原文最大量，不另設房間總量配額。TotalBytes 可同交易維護以供統計；未來檔案須獨立配額。

32 KiB Body 是 JSON 傳輸位元組上限，8 KiB 是解析後原文 UTF-8 上限，分別驗證。極端 JSON 跳脫可能讓原文未超限但 Body 先超限，仍回傳明確錯誤，不截斷、不暗自調高上限。MVP 列表回傳整份快照，滿房時原文約 2.4 MB，加上 JSON 跳脫與欄位可能更大；不截斷單筆，實際大小與傳輸成本須量測。原「1 MiB／頁＋nextCursor」的分頁預算延後至恢復分頁時再訂。

## 4. 功能與非功能需求

| ID | 需求 |
| --- | --- |
| FR-01 | 建房原子建立 Room、房主 Participant 及 Code 保留紀錄，提交後回房碼、URL、expiresAt 與 Credential；不回傳半完成房。 |
| FR-02 | 六碼／QR 加入產生新 Participant；恢復用原 Credential。Client 不能指定 Owner／Creator；所有受保護 REST／Hub 驗證身分、RoomId、有效性。 |
| FR-03 | 多筆文字以穩定排序一次列出、新增、複製、刪自己；保留原文。剪貼簿權限被拒時可選取手動複製。列表顯示作者代碼或「你」，房主加上「(房主)」。輸入框旁常駐自己的作者代碼。同作者在目前顯示清單中連續發言、相隔至多五分鐘且中間無日期分隔線時，視覺上只在首則顯示作者。 |
| FR-04 | 房主關房提交後拒絕新操作；以通知及狀態檢查使他人 UI 失效。Worker 停止仍不能讀寫過期房。 |
| FR-05 | 方案 A 合併通知後重取整份快照，不每則各發一次 GET；取得後整體替換；無逐筆事件重放。 |
| FR-06 | 顯示載入、同步中、斷線、限流、結果不確定、身分失效及房間失效。寫入不受同步狀態限制，直接向 REST 送出，且不排離線操作。已顯示文字仍可複製。 |
| NFR-01 | HTTPS/WSS、純文字呈現／CSP、敏感資料遮蔽、API／Hub 等敏感回應 `no-store`；無跨房間資料與舊回應污染。匿名不是端對端加密。 |
| NFR-02 | DB Source of Truth，提交失敗不通知；提交成功後通知失敗不回滾或假報失敗；重複／亂序通知最多影響重抓頻率，不改本機資料內容。 |
| NFR-03 | 1,000 連線／200 房／典型 5 人測試，另測熱房、同 NAT、併發、滿 300 筆、大量重連與 REST 補償流量；容量未驗證。 |
| NFR-04 | 遺漏修復依 Q6；REST p95 ≤ 500 ms、提交到畫面 p95 ≤ 2 秒為候選效能值，不是已實測承諾。需記錄 429／5xx、合法操作成功率及讀取放大，不能靠拒絕正常流量達標。 |
| NFR-05 | 單實例無 HA SLA；app 重啟可恢復 DB 現況；DB 不可用明確失敗，永久資料損毀無產品層還原承諾。 |
| NFR-06 | body、回應、請求、DB／Hub 連線、limiter partition、Client 工作與記憶體均有界；同房短交易、索引及可觀測性；重要邏輯有實測。 |

## 5. 初始安全與資源設定（待安全測試與壓測調整）

部署以自架 Docker Compose 為主。AWS、Seq 與外部 SaaS 監控為選配。任何不受信任的裝置或使用者能連線的環境均視為公開部署，包含可由訪客 Wi-Fi 或 BYOD 存取的內網。受信任內網亦須保留驗證、限流及敏感資料保護。S7 完成前不得公開部署。 [ADR 0004](decisions/0004-observability-and-deployment.md)

日誌、Tracing、Metrics 與代理診斷須防止揭露六碼房碼、Credential（含 SignalR `access_token`）、使用者文字、Authorization、Cookie 及 DB 密碼。完整 HTTP Body 排除在收集範圍外。實際 URL 中的房碼與 token 須移除，metric 只使用低基數路由模板。RoomId 可用於日誌關聯，不能用作 metric tag。各階段驗證實際輸出。

健康檢查區分程序存活與 DB 就緒，清理 Worker 狀態另行回報。部署啟動時一次檢查 schema，確認不相容須以非零碼結束。靜態入口頁重新驗證，有內容 hash 的資產可長期快取。 [Sprint 1 實作計畫](sprint-1-implementation-plan.md)

以下都是可設定的實驗起點。依 [ADR 0003](decisions/0003-mvp-scope-reduction.md)，MVP 只實作「公開建房」、「公開加入／猜碼」的 token bucket、「已驗證 REST」的新增／刪除限制、「每房寫入／新 Participant」的 Participant 上限與「DB pool」；其餘列於 S7 依安全測試／壓測補齊，補齊前不發布公開站台。Token bucket 表示持續補充速率與最大 burst；時間窗／失敗計數另明定。拒絕回 429／Retry-After；過載回 503。公開入口不新增獨立的「查房是否存在」介面，查詢／加入共用防護。

| 層 | 候選初始值 | 理由／限制 |
| --- | --- | --- |
| 公開建房 | 每 IP 每分鐘 30 次，burst 10；全站每分鐘 120 次，burst 20（token bucket） | 建房有資料成本，但共用 NAT 下多個小組仍需能分批建房；1,000 人／200 房壓測須記錄暖機過程，不能把此額度當既有身分的讀取限制 |
| 公開加入／猜碼 | 每 IP 每分鐘 120 次，burst 30；全站每秒 30 次，burst 60（token bucket），成功／失敗皆計入 | 來源 burst 與每房 burst 同為 30，讓同一 NAT 的 30 人可同時加入。千人新加入需分批暖機 |
| 連續無效嘗試 | 依 IPv4 /32、IPv6 /64 分組，滾動 10 分鐘內累積 20 個不同的無效格式／不存在／失效碼後，公開 Join 冷卻 60 秒；重犯依序退避到 120、240、300 秒 | S7-2 將冷卻範圍限定為公開 Join。建房不接收房碼，限制建房無助防猜碼，也會影響同 NAT 的房主。已驗證 REST 與 Hub 維持各自的配額 |
| 跨來源防護 | 全站滾動 1 分鐘累積 300 次實際查詢後的查無／失效結果時，公開 Join 降為每秒 2 次、burst 5，至少 60 秒；同一視窗低於門檻後以每秒 10 次、burst 20 恢復 60 秒，再回到原額度 | 恢復階段再次達門檻會重新開始至少 60 秒的壓力模式。全站逐筆計算查詢結果，反映 DB 負載。格式無效在查詢前拒絕，只計入來源。被冷卻或任一前置桶拒絕的請求不計入 |
| 已驗證 REST | 每 Participant 讀取每分鐘 240 次／burst 40，新增每分鐘 30 次／burst 10，刪除每分鐘 60 次／burst 10（token bucket）；每 IP 合計每分鐘 30,000 次／burst 1,000 作粗上限 | 為輪詢及分頁補償留餘裕；不把 IP 當身分，新加入可換 Participant，仍需房間與全站保護 |
| 每房寫入／新 Participant | 文字寫入每分鐘 120 次／burst 20；Join 每分鐘 30 次／burst 30；生命週期 Participant 最多 1,000 筆（房主計入） | 前兩者採 token bucket。Join 以內部 Room ID 計算，先在房間交易鎖內檢查有效性和生命週期上限，成功寫入新 Participant 後、送出 commit 前才扣額度。原 Credential 恢復沿用 /me |
| SignalR 連線 | 全站 1,500、每來源 30（IPv4 /32、IPv6 /64）、每房 100、每 Participant 3 條同時連線 | negotiate 預檢，WebSocket 帶協商取得的 `connectionToken` 作為 `id`，`OnConnectedAsync` 原子取得名額。同 NAT 超過 30 條時，額外的用戶端繼續以 REST 輪詢 |
| 協商／Hub 訂閱 | 已驗證協商每來源每秒 50 次／burst 100、全站每秒 100 次／burst 200；`SubscribeRoom` 每連線每分鐘 10 次／burst 3（token bucket） | 缺少或格式錯誤的 Credential 直接回 401，且不扣協商額度。其他驗證前流量沿用 S7-1。協商來源表上限 20,000 筆，閒置 10 分鐘後回收；表滿時，表外來源仍受全站協商速率限制 |
| HTTP 並行 | 全站 96 個 in-flight；建房／加入子上限 16、新增文字 32、讀取／刪除等 48；超限不排隊 | SignalR 保留 negotiate，transport 僅允許 WebSockets。S7-4 可分別計算協商等短 HTTP 工作與常駐 Socket，省去 SSE／Long Polling 的 HTTP 生命週期計算 |
| DB pool | API pool Max 64、Worker 獨立受限 pool Max 4，最小皆 0；兩者合計最多 68，另留維運連線 | 不是每個 DbContext 都開新 pool；部署 PG max_connections 須涵蓋合計與餘裕，需測等待／鎖逾時 |
| 部署與記憶體 | 起始 app／DB 各 4 vCPU、8 GiB；DB volume 建議至少 20 GiB；磁碟 70% 警戒、85% 暫停新建／加入／新增，保留清理空間 | 部署／檔案系統能力須確認；CPU／記憶體持續高水位也降低 admission。不是精確全站資料配額，刪除亦可能耗 WAL，不能等到滿碟才動作 |
| 有界 limiter 狀態 | 全站最多 20,000 個活躍 partition／失敗計數鍵，閒置 10 分鐘回收；S7-2 的冷卻表每程序最多 20,000 個來源 | 僅可信 IP、已驗證內部 ID 及固定全站鍵。冷卻表滿載時，表外來源暫停冷卻追蹤，仍套用每來源與全站 Join 桶、全站壓力限制及解析後的每房額度。憑證失敗計數滿載時，新來源退回 DB 保護底線。其他 admission 的有界分區留待 S7-4 |

S7-2 的來源冷卻以正規化後的房碼去重，規則與 Join 查詢共用（去除前後空白並轉為不分地區的大寫）。每來源的資料上限為 536 bytes，其中 480 bytes 是至多 20 組 128-bit SHA-256 截短雜湊與單調時鐘時間戳，24 bytes 是計數、到期和退避欄位，32 bytes 是來源鍵。執行環境的物件與字典配置開銷另計。冷卻解除後，仍在 10 分鐘視窗內且保留的指紋繼續去重。若視窗仍滿 20 筆，新錯碼會取代最舊指紋，並立即以重犯退避回 429。被取代的房碼即使仍在原 10 分鐘內，於冷卻結束後重送也會重新計入。若已有指紋過期，視窗重新達到 20 筆時啟動下一輪冷卻。上次新錯碼已過 10 分鐘時，退避從 60 秒重算。已到期且閒置滿 10 分鐘的來源才可被回收。每個程序在滿載時最多每 60 秒記錄一次 EventId `6103`，只含跳過追蹤的筆數。日誌及指標不含房碼或雜湊。

冷卻、壓力／恢復期間超額及每房超額均回 429 `RateLimited`，包含 `Retry-After` 與 `Cache-Control: no-store`。每房以每 2 秒補一格，桶滿後可回收。房間失效、已滿、寫入失敗或憑證恢復均不扣額度。扣額度後若提交失敗或結果不確定，保留已扣的額度，由補充速率恢復。

S7-2 剩餘風險

- 攻擊者以大量 /64 輪流送錯碼，可在恢復期反覆觸發壓力，使全站長期處於壓力模式。壓力期間若攻擊者持續送出每秒 2 次以上的請求，大部分合法 Join 會被拒，使用者需要重試。S8 量測壓力模式下合法 Join 的成功率與延遲。
- 程序重啟會重置冷卻、壓力及每房 Join 桶。
- 知道分享連結的人仍可耗盡 1,000 個生命週期名額。單靠公開 Join 耗盡新房的 999 個剩餘名額至少約需 32 分鐘，房主可關房重建。身分以分頁為單位，新分頁、其他裝置或無痕視窗均會消耗新名額。

S7-3 的來源、房間及 Participant 連線計數只在持有名額時存在，歸零即移除。網路閃斷時，舊連線可能仍佔用名額，直到 SignalR 依 `ClientTimeoutInterval`（預設 30 秒）判定逾時。協商名額超額預設回 `Retry-After: 30`，和逾時共用 `Api:Hub:ClientTimeoutSeconds`。調整逾時時也須同步調整 Close 路徑的前端退避基準。WebSocket 無法建立時維持既有連線狀態提示及 REST 輪詢。

部署警戒與 admission 必須有恢復條件及遲滯，避免臨界值反覆切換；精確門檻由負載測試確認。使用可信 Reverse Proxy allowlist 處理 forwarded headers，防自填 X-Forwarded-For。HTTP limiter 不限制每次 WebSocket invocation，Hub 方法另驗證與計數。

MVP 不設每次寫入競爭的全站資料庫配額列，不用 TotalBytes 實作全站精確硬配額。先以並行、pool、連線及部署硬限制＋觀測保護；記憶體內配額只對單實例成立，不宣稱可完全防止分散式猜碼或在多副本保持全域一致。

## 6. 驗收條件

| ID | 場景與預期 |
| --- | --- |
| AC-01 | 建房／房主／保留碼原子成功或全回滾，Code 碰撞有界重試，不回他人房間。跨房 Owner／Creator 被 DB 複合 FK 拒絕。 |
| AC-02 | 手輸／URL／QR 加入；QR 無 Credential，手機非房主；F5 恢復不建新 Participant。主動離開只適用於非房主，會清除本分頁身分。遺失身分不恢復舊權限；Session Restore 不誤稱已銷毀。 |
| AC-03 | A／B 各建一筆，只刪自己；房主也不能單刪他人的文字，但可關房。改 RoomId、itemId 或 Owner／Creator 不繞過授權。 |
| AC-04 | 8,192 原文 bytes 可接受、8,193 拒絕；含中文字／emoji／換行／原始空白，無靜默截斷；空白拒絕；Body 32 KiB 與原文限制分測，超 body 回 413。 |
| AC-05 | 有空位時 200 筆各 8 KiB（總計超過舊 1.5 MiB）仍可新增；TotalBytes 正確但不參與房間容量拒絕。第 301 筆拒絕。299 筆時併發新增只有一筆成功。 |
| AC-06 | 併發刪同筆只扣一次計數／Version，刪除 Owner 在後端驗證；關房／加入／新增／刪除／清理同鎖序，不留下孤兒、負計數或跨房資料。 |
| AC-07 | 列表一次回傳全部有效文字，CreatedVersion DESC、Id DESC 穩定排序；items、itemCount 與 snapshotVersion 來自同一交易；滿房 300 筆×8 KiB 完整回傳不截斷；查詢限定 RoomId 並使用排序索引。 |
| AC-08 | 先訂閱後載入；重複／亂序通知合併，單一進行中同步。只整體替換快照，不逐筆套事件；不要求版本連續；舊 GET／換房／離開／關房後晚回應不能污染畫面，舊 401／404 也不能清除新房狀態。 |
| AC-09 | commit 後通知前當機、最後通知遺漏、重連及回前景皆可由 REST 修復；依 Q6 量測 20～30 秒主要目標與約 60 秒容許上限。 |
| AC-10 | 到期 5 小時／提前關房即拒絕後續讀寫／訂閱，即使 Worker 停用。期限依鎖後 DB 檢查點，Client 時鐘與活動不延長；邊界及等待鎖後過期均測試。 |
| AC-11 | 每分鐘清理、每批最多 100，健康情況目標失效後 10 分鐘內刪 Rooms／Participants／items；重跑／停機補清無誤，backlog 與交易時間可觀測。 |
| AC-12 | 保留碼至少至原 ExpiresAt + 24 小時，提前關房／主資料 cascade 不提早釋放；到期且無 Rooms 引用才回收；重配後舊 UUID／Credential 無新房權限。 |
| AC-13 | MVP：建房／加入超額回 429、偽造 proxy header 無效、無效 Credential 無法訂閱 Hub；無文字／Credential 日誌、HTML 純文字呈現。S7：無效碼連續／分散猜測、同 NAT 合法流量、partition 耗盡與 Hub invocation 濫用有預期防護。 |
| AC-14 | 1,000 連線／200 房、熱房、滿 300 筆×8 KiB、重連與 REST 補償壓測；記錄硬體、設定、合法成功率、429／5xx、p95、修復延遲、DB／CPU／記憶體／磁碟。不能靠拒絕正常流量達標。 |
| AC-15 | DB 失敗不假報成功／發事件；通知失敗不回滾提交；POST 回應遺失提示結果不確定並檢查狀態，不自動重送。API 明確區分驗證、權限、失效、限流及過載。 |

## 7. 仍需實測的設定與實作邊界

產品規則不再待定；需實測的是 FK／EF 寫入與刪除行為、同房競爭與期限、整份快照的回應大小、方案 A 在熱房的重抓成本、限流／NAT 取捨、Worker 吞吐與資源配置。候選壓測基準：每房起始 50 筆、平均 1 KiB、全站每秒 20 次平衡新增／刪除、觀察 30 分鐘，再測 8 KiB／300 筆與階梯負載；這些數值仍可依測量調整。

Phase 2 檔案方向另見架構：Object Storage 存內容、PostgreSQL 存 Metadata、沿用所有權與期限、獨立配額及可重試清理。本次不建 RoomFiles、IFileStorage、檔案 API 或套件。

實作進度以 [Sprint 1 實作計畫](sprint-1-implementation-plan.md) 為準。
