# Pasto — 開發進度與下一步

更新日期：2026-11-18（Asia/Taipei）。這是目前狀態的入口；開工前先讀 [AGENTS.md](AGENTS.md)，並以實際 Git 狀態核對本檔。詳細設計與驗證紀錄以連結文件為準。

## 目前位置

| 項目 | 狀態 |
| --- | --- |
| 目前分支 | `feature/s3-01-item-write-api` |
| 已合併基準 | `main` 的 `9eace5f`，PR #5：完整 S2 |
| S3-01 功能提交 | `408593e`：文字新增／刪除 API 與 Participant 寫入配額 |
| 最新程式提交 | `ad5ff82`：測試連線池與清理修正 |
| 遠端狀態 | S3 尚未推送、開 PR 或合併 |
| 當前交付點 | `ad5ff82` 的獨立 review，範圍 `408593e..ad5ff82` |

開工前以 `git status` 確認工作目錄狀態，不覆蓋未提交的修改。不得自行推送或合併，除非使用者明確授權。

## 階段進度

- [x] S1：正式持久層、交易核心、併發與期限邊界，已合併。
- [x] S2：建房、Join、Bearer、me、metadata、關房 API，已合併；使用者已確認測試審查 OK。
- [x] S3-01 實作：POST／DELETE items；新增與刪除配額分開，DELETE room 與刪除文字共用刪除配額。
- [x] S3-01 review finding 修正：fixture 改用自有、有上限的 NpgsqlDataSource pool，先關 pool 再清獨立 schema；新增 3 例回歸測試。
- [ ] S3-01 修正的獨立 review／審查方驗證。產品程式審查無阻擋問題；測試設施修正仍待確認。
- [ ] S3-02：整份文字快照列表，尚未開始。
- [ ] S4：基本前端 UI。
- [ ] S5：SignalR 通知與同步驗收。
- [ ] S6：到期／關房資料清理。
- [ ] S7：防護、資源與濫用處理。
- [ ] S8：壓測與收尾。

## 最新驗證

以下為實作方結果，尚非修正後的獨立審查證據：

- Release Build：0 warning、0 error；format verify、diff check 通過。
- 相關連線池／併發測試：11 例通過。
- 同一命令緊接兩輪完整測試：各 **269 passed、0 failed、0 skipped**。命令為 `dotnet test Pasto.slnx -c Release --no-build --no-restore`（各輪加 TRX logger）；需先啟動 Docker Compose 並設定 `PASTO_TEST_CONNECTION_STRING`，步驟見 [README](README.md)。
- TIME_WAIT 取樣：修改前單輪峰值 4,837；修改後第一輪 544，緊接第二輪累積峰值 1,088。
- 測試後殘留 schema：0。
- 先前 socket 連線失敗、測試修正及取樣限制均保留於 [testing.md](docs/testing.md)。未刻意重現埠耗盡，不宣稱已排除所有根因。

目前不需要使用者額外手動測 API。若後續需要，應提供具體步驟與預期結果。

## 下一個工作

**下一個對話的角色**：Claude Code 以 Reviewer 身分獨立審查 `408593e..ad5ff82`，重點為測試設施修正（fixture 連線池、schema 清理、3 例回歸測試）；產品程式先前審查無阻擋問題。Review 結果尚未產生，完成後記錄於本檔與 [S3 工作項](docs/s3-items.md)。在使用者明確核准前，任何 Agent 都不得開始實作 S3-02。

1. 先確認 `ad5ff82` 的 review 結果；有 finding 時先驗證並處理，不跳過交付點開始 S3-02。
2. 使用者核准後，由 Implementation Agent 依 [S3 工作項](docs/s3-items.md) 實作 GET `/api/rooms/{roomId}/items`。
3. 共用 read admission 與 Participant 讀取配額；以唯讀 REPEATABLE READ 取得一致的房間狀態與全部文字，版本以字串回傳。
4. 排序為 `CreatedVersion DESC, Id DESC`；不做分頁、cursor、單筆 GET 或內容截斷。
5. 驗證空房、授權／失效、跨房隔離、寫入競爭與快照一致性；300×8 KiB 資料經正式 AddItem 建立，量測回應大小並檢查排序索引。
6. 完成小 patch、測試與本機提交後交獨立 review，更新本檔及驗證紀錄。

## 後續必須保留的事項

- S5：新增／刪除／關房的通知只能在提交成功後發送；依既定同步契約驗收，重新連線由 Server 重新同步。
- S7：少數來源可耗盡共用的驗證前 read admission，影響讀取，也會阻擋新增文字、刪除文字與關房。Participant 寫入配額不會緩解此入口風險。
- S7：持有分享連結者可反覆 Join 耗盡 1,000 個生命週期 Participant 名額。
- **S7 防護補齊前不得公開部署**；已記錄風險不代表已緩解，不自行調整既定配額。

## 文件入口與維護

- [AGENTS.md](AGENTS.md)：長期開發規範。
- [README](README.md)：本機環境、Docker Compose 與測試指令。
- [S3 工作項](docs/s3-items.md)：當前 patch 範圍、契約與驗收。
- [測試紀錄](docs/testing.md)：實測證據、失敗與環境資訊。
- [分階段計畫](docs/sprint-1-implementation-plan.md)、[架構](docs/architecture.md)、[需求](docs/requirements.md)、[ADR 0003](docs/decisions/0003-mvp-scope-reduction.md)：詳細設計與既定取捨。

每個 patch 提交、review 結果或合併完成後更新本檔；保留目前狀態與下一步，歷史細節移至對應文件，避免維護多份完整設計或測試日誌。
