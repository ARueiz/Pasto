# ADR 0003：MVP 範圍收斂——正式持久層優先、整份快照、基本限流

- 狀態：Accepted
- 日期：2026-11-05
- 修訂：[ADR 0002](0002-room-code-and-anonymous-participants.md) 中的分頁協定與分層限流的 MVP 範圍；其餘決策不變
- 現行規格：[requirements.md](../requirements.md)、[architecture.md](../architecture.md)、[Sprint 1 實作計畫](../sprint-1-implementation-plan.md)

## 背景

ADR 0002 與 2026-10-20 的需求／架構把正確性、同步與防護的規格一次寫到接近正式上線的完整度。S1-01 已用真 PostgreSQL 證明 Creator 複合 FK 與 deferred 檢查可行，但目前尚無任何產品程式。

檢視後發現三個問題：

1. S1 的整合測試驗證的是測試專用的 `schema.sql` 與 `RoomTestDbContext`。若 S1-02～S1-04 繼續在測試內擴充寫入路徑，產品出現後會有兩套 schema 與交易邏輯，測試證明的不是實際上線的程式。
2. Keyset 分頁、簽章游標、同版本跨頁組裝、`SnapshotChanged` 重試與 `requestedCount` 是前後端最複雜的部分，但每房上限 300 筆 × 8 KiB，原文最大約 2.4 MB，典型房間遠小於此。
3. 需求第 5 節列出十餘層限流與 admission 候選值，MVP 一次實作會延後可用版本，且多數數值本來就待壓測決定。這與 AGENTS.md「避免過度設計」衝突。

## 決策

### 1. 正式持久層優先，測試改跑產品程式

- S1-02 起不再擴充測試專用 mapping 或寫入 helper。先建立正式 `src/Pasto.Server` 的 Infrastructure（DbContext、EF Core Migration），整合測試改為對 Migration 建出的 schema 執行正式 use case。
- EF 無法表達的約束（DEFERRABLE 複合 FK、CHECK、部分索引）在 Migration 內以 `migrationBuilder.Sql()` 明確加入，並由整合測試檢查 `pg_constraint` 等實際結果。
- S1-01 的 `schema.sql` 在正式 Migration 涵蓋相同不變條件、既有兩個案例改跑正式 schema 並通過後移除。
- 資料庫必須保障的不變條件（同房 Owner／Creator、原子建房、cascade、計數／Version 同交易）完全不變，只改變驗證的對象。

### 2. MVP 文字列表回傳整份快照

- `GET /api/rooms/{roomId}/items` 在一個短唯讀交易內回傳該房全部有效文字、`snapshotVersion` 與 metadata，排序 `CreatedVersion DESC, Id DESC`。不提供 `limit`／`cursor`。
- 移除 MVP 的簽章游標、跨頁同版本組裝、`409 SnapshotChanged`、`requestedCount`、「載入更多」與部分載入狀態。
- 方案 A 保留：合併 `RoomChanged` 提示、單一進行中同步、generation／AbortController／request 序號防舊回應，以及 15～20 秒加 jitter 的 metadata 檢查。差別只在每次同步重抓整份快照。
- 回應最大約為 300 × 8 KiB 原文加 JSON 跳脫與欄位，可能超過原本 1 MiB／頁的候選預算，MVP 接受這個上限並在壓測量測；回應仍為 no-store。

### 3. MVP 只做基本限流與資源保護

MVP 必做：

- ASP.NET Core 內建 Rate Limiter：公開建房、公開加入各自的每 IP 與全站 token bucket（成功與失敗皆計入）。
- 已驗證 Participant 的新增／刪除限制；JSON Body 32 KiB 與原文 8 KiB 的既有驗證。
- DB pool 上限、可信 Reverse Proxy 的 forwarded headers 設定、日誌脫敏、no-store。
- 每房 Participant 生命週期上限 1,000（鎖內檢查，屬資料預算）。

延到 S7，依壓測／安全測試結果決定是否與如何實作：

- 連續無效嘗試冷卻與退避、全站失敗壓力模式。
- HTTP 分類並行上限、限流 partition 數量上限與閒置回收。
- SignalR 連線／協商／`SubscribeRoom` 細部配額（MVP 只做 Hub 方法的身分與房間驗證）。
- CPU／記憶體／磁碟警戒與 admission、遲滯恢復。

需求第 5 節的數值保留為候選起點。

## 取捨

| 決策 | 得到 | 代價／風險 | 重新評估條件 |
| --- | --- | --- | --- |
| 正式持久層優先 | 測試證明的就是上線程式；不維護兩套 schema | S1 出口前就要先決定產品專案結構與 Migration 工具鏈；Migration 內的手寫 SQL 需要 Review | 若 EF Migration 無法穩定表達不變條件，改以版本化 SQL 腳本並沿用相同測試 |
| 整份快照 | 前後端同步邏輯大幅簡化，沒有跨頁版本衝突 | 熱房或滿房時每次同步傳輸量較大；1,000 連線同時重抓會放大讀取 | 壓測顯示頻寬、DB 讀取或 p95 不符目標時，以新 ADR 恢復 Keyset 分頁；現有 `ix_items_order` 索引可直接沿用 |
| 基本限流 | 較快得到可用 MVP；配額依實測決定，不先猜 | 分散式猜房碼、資源耗盡的防護較弱 | **補齊 S7 前不發布公開站台**，只在本機或隔離環境使用 |

## 不變的部分

ADR 0002 的身分、權限、期限、容量、交易、Row Lock、方案 A 的提示合併與舊回應保護、清理 Worker 與保留碼冷卻全部沿用。

另外確定 Sprint 1 以單一 `src/Pasto.Server` 專案起步，以資料夾區分 Api／Application／Domain／Infrastructure，並加入 `.slnx` 與本機 PostgreSQL 的 `compose.yaml`。依賴方向暫以慣例與 Code Review 維持；若出現跨層引用問題，再把 Domain 拆成獨立專案。
