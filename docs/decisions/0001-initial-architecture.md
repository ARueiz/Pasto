# ADR 0001：Pasto 初始架構提案

- 狀態：Withdrawn（未核定的歷史提案；由 ADR 0002 接續）
- 日期：2026-10-19
- 沿革更新：2026-10-20；現行方向見 [ADR 0002](0002-room-code-and-anonymous-participants.md)。
- 範圍：Sprint 1 需求分析與架構規劃
- 相關文件：[需求規格](../requirements.md)、[架構設計](../architecture.md)

> 以下保留 2026-10-19 的原提案內容供比較，不是現行規格。此提案從未被核定，因此不標為「已接受後被取代」。128-bit Room Token、人人可刪單筆、24 小時、100 筆、500 連線與舊同步方式等內容，均應以 ADR 0002 及現行需求／架構文件為準；基礎分層與資料交易原則仍沿用。

## 背景

Pasto 的 MVP 是匿名多人即時共享剪貼簿：建房分享、讀取多筆文字、複製、新增／刪除同步，以及到期與資源限制。專案也用於學習系統設計與併發處理，需要讓設計容易理解、驗證及演進。

使用者已指定 React + TypeScript + Vite、ASP.NET Core .NET 10、PostgreSQL + EF Core、SignalR、Docker Compose 與 Modular Monolith。這些是本提案的前提；以下比較用於說明其適用性，不擅自替換技術。沒有既有應用程式或負載量測可據以做容量保證。

## 提議採用的設計

一個 Rooms 模組與一個部署的 ASP.NET Core 應用，按 API、Application、Domain、Infrastructure 分層。PostgreSQL 是 Source of Truth；REST 執行讀寫，SignalR 只通知已提交的變更。以高熵 capability 連結支援匿名存取，同房間短交易列鎖保護容量，Client 以版本化 REST 快照收斂狀態。固定期限與限制數值仍待需求 Review。

## 選擇、替代方案與 Trade-offs

| 主題 | 提案與用途 | 替代方案 | 取捨與何時重評估 |
| --- | --- | --- | --- |
| 前端 | React Functional Components、Hooks、TypeScript、Vite，處理房間 UI 與連線狀態 | Razor／Blazor、其他 SPA 工具 | 符合既定技術與學習方向；需管理非同步狀態及前後端契約，也增加前端建置流程。SSR 並非此 MVP 的必要需求。 |
| 後端 | ASP.NET Core .NET 10、async/await、DI、Structured Logging | Node.js 等其他平台 | 與既定技術及 SignalR 整合一致；仍須理解 DB 交易、取消與連線資源，不靠 async 自動解決容量問題。 |
| 架構 | Modular Monolith，一個 Rooms 業務模組、四層責任 | 無分層單體、Microservices | 避免 UI／Hub 混入資料規則，同時保留單一部署與交易；有介面維護成本且模組不能獨立擴展。尚無服務拆分需求，不引入分散式交易。 |
| 儲存 | PostgreSQL + EF Core，關聯約束、短交易與索引 | SQLite、記憶體／Redis、文件型 DB、直接 SQL | PostgreSQL 適合持久化及併發；EF Core 提高一般資料存取可讀性，但仍需理解產生的 SQL，列鎖等操作可能使用參數化 SQL。不以 Redis 取代 Source of Truth。 |
| 傳輸 | REST 讀寫＋SignalR 事件通知 | 全部經 Hub、純輪詢、SSE／原生 WebSocket | REST 讓交易、錯誤及重同步清楚；SignalR 提供即時連線抽象，仍增加常駐連線成本。純輪詢簡單但延遲／查詢量較高；自管 WebSocket 需額外處理連線協定。 |
| 匿名存取 | 128-bit CSPRNG token 的連結，伺服器存 SHA-256 雜湊 | 短數字房碼、帳號／ACL、另加密碼、URL fragment token | 高熵可降低猜測風險且不需帳號；無個人歸屬／單人撤銷，URL 仍會外洩。fragment 可減少路徑紀錄曝光，但改變路由／分享體驗，列為 Q7。 |
| 併發控制 | 同房間先鎖 Rooms 列，再驗證並更新 items、計數及 Version | 先查筆數再寫、程序內 lock、樂觀版本重試、Serializable | 列鎖讓跨列容量規則容易驗證，跨程序仍有效；同房間寫入會序列化。樂觀重試在熱房衝突更多；Serializable 仍需處理失敗重試。若熱房成為實測瓶頸再重評。 |
| 同步恢復 | 小型新增／刪除事件作失效提示，版本化 REST 快照＋前景定期檢查 | 直接套用所有事件、完整事件回放、outbox＋重送 | 可修復通知遺漏且不需事件儲存；增加讀取、輪詢及熱房分頁重試。若要求持久事件交付或極低修復延遲，需增加 outbox／增量協定，不能把現有方案說成 exactly-once。 |
| Rate Limiting | 內建 middleware＋Hub 方法／連線保護，IP 與有效房間及全站限制 | 只有前端限制、僅 IP、共享分散式 limiter | 單程序起步成本低；匿名可換 IP、NAT 會誤擋，且非跨副本全域配額。多實例時需重新設計，不預先加 Redis。 |
| 到期 | 請求時拒絕到期資料，Worker 索引分批實體刪除 | 只靠排程刪除、每請求大量清理、活動延長 TTL | 存取失效不依賴清理時程，批次操作可控；必須處理寫入／清理競爭及備份保留。固定或滑動期限屬商業規則，固定 24 小時只是建議。 |
| 本機環境 | Docker Compose 規劃整合 app／DB | 全部手動安裝、Kubernetes | 易於建立一致的本機環境；Compose 不自動提供 HA、備份或正式維運能力。此次不建立 Docker 檔案或啟動服務。 |

上述框架限制與 DB 行為的來源分別見官方 [SignalR scaling](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0)、[Rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)、[PostgreSQL locking](https://www.postgresql.org/docs/current/explicit-locking.html)。表中方案與重評估條件是本專案的設計判斷。

## 一致性與可靠性界線

1. 同房間新增／刪除／計數／版本在同一 DB 交易，提交後才發通知。通知呼叫不放在持鎖交易內。
2. DB commit 和 SignalR 發送之間存在失敗窗口；通知可以遺漏、重複或亂序。單純呼叫成功也不代表所有瀏覽器已套用。
3. Client 先訂閱再取同版本分頁快照；重連後重新訂閱、重新同步。定期版本檢查處理最後一則事件遺漏。沒有持久事件回放。
4. 新增 POST 回應遺失時不自動重試；若要保證安全重送，需新增持久冪等鍵規格。相同文字本身不是冪等鍵。
5. 到期依 DB 時間在授權／鎖內檢查點判定。到期前接受的短交易與在途回應可能稍晚完成；實體清理與備份移除是另一個時程。

## 正面影響

- 維持單一部署與關聯交易，讓 MVP 與學習範圍可控。
- 業務規則不綁在 HTTP／SignalR／EF Core，重要容量與到期規則可分別測試。
- 高熵連結、有限資料量、有界查詢與明確併發控制，先處理大量使用者下常見的風險。
- 通知遺漏不等於永久資料缺漏；以 DB 現況恢復，不依賴 Client 保存完整事件歷史。

## 成本、限制與接受前提

- 匿名連結是共用權限，洩漏後無法只移除某人；所有人可刪除的建議可能不適合機密或重要資料。
- 單實例有停機與連線容量上限。同房間寫入會競爭一列鎖；通知造成多 Client 讀取放大，輪詢也有固定成本。
- 分頁版本衝突需要重取，持續高頻變動時可能長時間處於同步中。大量使用者不等於允許單一房間無限流量。
- 全域容量預算需另選原子配額或保守預算＋部署硬保護，不能只靠房間鎖；這是實作前待決事項。
- 備份／WAL、代理日誌與 Client 副本有各自保留生命週期，不能用房間 TTL 承諾所有副本同步消失。
- 提案目標未經壓測。正式環境的版本固定、TLS、secret、備份及復原方式需在後續實作／維運規劃補齊。

## 演進觸發條件

先觀察延遲、DB 鎖等待、查詢計畫、連線數、記憶體、通知失敗、快照重試與清理延遲。達到核定預算後先調整索引、限制、讀取合併與單實例資源。

只有需要多實例／高可用時才另立 ADR，比較 SignalR 託管服務與 backplane，處理跨實例廣播、路由親和、共享限流與連線配額。只有需要可靠事件交付時才比較 PostgreSQL outbox；不因預期未來成長就立即加入 Microservices、Redis 或 Message Queue。

## 待 Review 決策

需求文件 Q1–Q7 為商業規則的單一待確認清單，優先確認匿名刪除權限、到期與資料保留、資源配額、同步修復時間與連結曝光風險。另需確認是否接受列鎖與快照的限制，以及全域容量保護方案。

歷史註記：原提案當時為 Proposed，未曾核定；2026-10-20 Review 後撤回並由 ADR 0002 記錄現行方向。本 Sprint 仍不開始應用程式實作。
