# S1-05：期限邊界與 S1 驗證對照

日期：2026-11-17。已從 GitHub pull main 至 PR #3 的 merge commit `2853cda`，並將 `feature/s1-05-expiry-boundaries` 快轉至相同基準。本階段補齊 S1-04 延後的期限證據；只有測試與文件變更，不修改產品程式、schema、Migration 或套件。

## 期限語意

- 新增、刪自己的文字與關房，均須先取得 Rooms row lock，再另查 DB 當下時間並驗證有效性。
- `DeletedAt` 非 NULL，或檢查時間大於等於 ExpiresAt，都回 RoomUnavailable；拒絕時不改 items、統計或版本。
- 等鎖前有效不代表取得鎖後仍有效；不可用交易起始時間 `now()` 取代 `clock_timestamp()`。
- 期限檢查點之前已被接受的短交易可能稍晚提交；本階段不新增「commit 前再驗一次」或到期撤銷在途交易的規則。

## 新增測試與證據界線

`RoomExpiryTests` 共 21 個真 PostgreSQL 案例，執行正式 AddItem／DeleteOwnItem／CloseRoom，資料由正式建房及新增 use case 準備。

### 精確比較邊界：18 例

三條路徑分別測試 ExpiresAt 前一微秒、相等、後一微秒，再交叉有效／已關房狀態。有效房僅前一微秒可成功；等於或晚於期限、或已關房，一律拒絕。

一般 OS 排程無法可靠命中 PostgreSQL 的同一微秒，因此測試 interceptor 只在正式時間查詢點，將 SELECT 改為同交易讀取 persisted ExpiresAt 加偏移。它先確認 FOR UPDATE 已完成、查詢仍在交易內，且每例只替換一次時間查詢。不替換 reader 結果、不在產品程式加入測試開關。

此組證明精確比較與 DeletedAt 的處理，**不將注入的比較時間宣稱為自然流逝的真實牆鐘證據**。成功案例驗證版本、筆數、bytes 與 CreatedAt／DeletedAt；拒絕案例驗證原文字及房間狀態保留，Participant 與保留碼不變。

### 真實等待鎖後到期：3 例

不注入時間查詢，保留正式 clock_timestamp。步驟如下：

1. 獨立 context／連線的 fixture 交易先鎖住房間。
2. 啟動正式操作，透過共用 hooks 與 pg_blocking_pids 確認確實進入鎖等待。
3. 從 pg_stat_activity 取得等待方 xact_start；持鎖交易用 DB clock_timestamp 將期限改為此刻，再提交釋鎖。
4. 斷言等待方交易起點早於新期限，且正式操作取得鎖後回 RoomUnavailable，文字、計數、Version、DeletedAt 均未被操作修改。

這是以 fixture 時間控制建立的競爭，不靠任意 sleep。若誤用 now()，會拿到早於 ExpiresAt 的交易起點而錯誤放行，測試即失敗；若在等鎖前取時間，也會錯誤放行。fixture 只調整房間時間，保留碼仍保留原建房期限；不代表產品允許更改房間期限。

## S1 出口條件對照

| 不變條件 | 證據 |
| --- | --- |
| 正式 Migration、複合 FK、deferred／cascade 與索引 | MigrationSchemaTests 查實際 PostgreSQL catalog、重套／回退及 schema 隔離 |
| 同房 Creator、房間必有房主、原子建房 | CreatorIntegrityTests 正反例、RoomCreationTests 分階段失敗／取消／commit 前後故障 |
| Owner 同房、FK 最後防線、刪房 cascade／保留碼 | OwnershipAndCascadeTests 的有效／跨房 commit、回滾與實際刪房 |
| 300 筆競爭、bytes／計數／版本同交易 | ItemAdditionTests 最後一格及一般併發；ItemDeletionTests 同筆重刪、不同筆刪除、容量釋放與新增／刪除兩種順序 |
| 有效性 → 同房 → 操作權限的檢查順序 | 三條路徑各自的失效／同房／Owner 或 Creator 案例 |
| 等鎖後關房、關房與寫入相同鎖序 | RoomClosureTests 的雙向競爭、併發關房與關房回滾後新增 |
| 到期前／相等／之後、DeletedAt、等鎖後到期 | 本階段 RoomExpiryTests 的控制邊界與真 DB 時鐘兩組證據 |
| commit 結果不確定不自動重試 | 既有建房／新增／刪除／關房的提交後故障與取消案例；只證明可能已提交，不宣稱模擬全部網路競爭 |

完整 120 例通過，S1-05 仍待獨立 review；不將實作方的執行結果宣稱為審查方獨立實測。核准前不開始 S2。

## 驗證與已知限制

- Release Build：0 warning／0 error；格式與差異檢查通過。
- 相關測試：21 passed／0 failed／0 skipped；完整測試：120 passed／0 failed／0 skipped。
- 命令、TRX 與清理紀錄見 [testing.md](testing.md)。
- 共用鎖觀察窗仍為 1.5 秒、產品 lock_timeout 為 2 秒；原 F3 穩定性風險保留，本輪未遇偶發失敗。
- 只驗證目前正式建房／新增／刪除／關房路徑。Join、HTTP／host DI、SignalR、清理 Worker 與負載證據尚未交付。
- 保留碼回收／清理競爭在 S6 驗證；本階段不實作 Worker，不宣稱到期資料已被實體刪除。
