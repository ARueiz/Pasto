# Pasto — 開發進度與下一步

更新日期：2026-11-18（Asia/Taipei）。這是目前狀態的入口；開工前先讀 [AGENTS.md](AGENTS.md)，並以實際 Git 狀態核對本檔。詳細設計與驗證紀錄以連結文件為準。

## 目前位置

| 項目 | 狀態 |
| --- | --- |
| S3 交付分支 | `feature/s3-02-item-snapshot`，由 `feature/s3-01-item-write-api` 的 `df2510d` 接續 |
| S3 開發基準 | `main` 的 `9eace5f`，PR #5：完整 S2 |
| S3-01 功能提交 | `408593e`：文字新增／刪除 API 與 Participant 寫入配額 |
| S3-01 P3 修正 | `df2510d`：補強連線池設定重設驗證 |
| S3 交付 | [PR #6](https://github.com/ARueiz/Pasto/pull/6) 包含 S3-01 與 S3-02，目標分支為 `main`，合併狀態以 PR 為準 |
| 當前交付點 | S3-02 獨立 review 已完成，無阻擋問題，P3 已補文件。審查範圍 `df2510d..a05d87f` |

開工前以 `git status` 確認工作目錄狀態，不覆蓋未提交的修改。不得自行推送或合併，除非使用者明確授權。

## 階段進度

- [x] S1：正式持久層、交易核心、併發與期限邊界，已合併。
- [x] S2：建房、Join、Bearer、me、metadata、關房 API，已合併；使用者已確認測試審查 OK。
- [x] S3-01 實作：POST／DELETE items；新增與刪除配額分開，DELETE room 與刪除文字共用刪除配額。
- [x] S3-01 review finding 修正：fixture 改用自有、有上限的 NpgsqlDataSource pool，先關 pool 再清獨立 schema；新增 3 例回歸測試。
- [x] S3-01 修正的獨立 review／審查方驗證：無 P0～P2；非阻擋 P3 已補強測試。
- [x] S3-02 實作：整份文字快照列表、唯讀交易與既有讀取配額。
- [x] S3-02 獨立 review：無阻擋問題，P3 補記最壞回應大小估算與後續量測。
- [ ] S4：基本前端 UI。
- [ ] S5：SignalR 通知與同步驗收。
- [ ] S6：到期／關房資料清理。
- [ ] S7：防護、資源與濫用處理。
- [ ] S8：壓測與收尾。

## 最新驗證

S3-02 審查方在 `a05d87f` 重跑相關 12 例及完整兩輪各 278 例，全部通過。Release Build、格式檢查通過，殘留 schema 0。TRX 核對結果見 [測試紀錄](docs/testing.md)。

審查方（Claude）獨立重跑，2026-11-18，`da98a83`：

- Release Build 0 warning／0 error；format verify 通過。
- 相關 `TestDatabasePoolingTests` 3 passed；同一命令緊接兩輪完整測試各 **269 passed、0 failed、0 skipped**。
- 殘留 schema 0。未重測 TIME_WAIT，以下取樣數字仍為實作方紀錄。

實作方結果：

- Release Build：0 warning、0 error；format verify、diff check 通過。
- 相關連線池／併發測試：11 例通過。
- 同一命令緊接兩輪完整測試：各 **269 passed、0 failed、0 skipped**。命令為 `dotnet test Pasto.slnx -c Release --no-build --no-restore`（各輪加 TRX logger）；需先啟動 Docker Compose 並設定 `PASTO_TEST_CONNECTION_STRING`，步驟見 [README](README.md)。
- TIME_WAIT 取樣：修改前單輪峰值 4,837；修改後第一輪 544，緊接第二輪累積峰值 1,088。
- 測試後殘留 schema：0。
- 先前 socket 連線失敗、測試修正及取樣限制均保留於 [testing.md](docs/testing.md)。未刻意重現埠耗盡，不宣稱已排除所有根因。

目前不需要使用者額外手動測 API。若後續需要，應提供具體步驟與預期結果。

## 下一個工作

S3 由 [PR #6](https://github.com/ARueiz/Pasto/pull/6) 交付。合併後，下一個工作項為從 `main` 接續 S4 基本前端 UI。S3-01 的非阻擋 P3 修正保留於 `df2510d`，S3-02 的最壞回應估算及審查證據見 [測試紀錄](docs/testing.md)。

## 後續必須保留的事項

- S5：新增／刪除／關房的通知只能在提交成功後發送；依既定同步契約驗收，重新連線由 Server 重新同步。
- S7：少數來源可耗盡共用的驗證前 read admission，影響讀取，也會阻擋新增文字、刪除文字與關房。Participant 寫入配額不會緩解此入口風險。
- S7：持有分享連結者可反覆 Join 耗盡 1,000 個生命週期 Participant 名額。
- S7／S8：實測以控制字元填滿 300 筆時的 JSON 回應大小與讀取頻寬。S8 另測 1,000 條連線同時重抓。
- **S7 防護補齊前不得公開部署**；已記錄風險不代表已緩解，不自行調整既定配額。

## 文件入口與維護

- [AGENTS.md](AGENTS.md)：長期開發規範。
- [README](README.md)：本機環境、Docker Compose 與測試指令。
- [S3 工作項](docs/s3-items.md)：當前 patch 範圍、契約與驗收。
- [測試紀錄](docs/testing.md)：實測證據、失敗與環境資訊。
- [分階段計畫](docs/sprint-1-implementation-plan.md)、[架構](docs/architecture.md)、[需求](docs/requirements.md)、[ADR 0003](docs/decisions/0003-mvp-scope-reduction.md)：詳細設計與既定取捨。

每個 patch 提交、review 結果或合併完成後更新本檔；保留目前狀態與下一步，歷史細節移至對應文件，避免維護多份完整設計或測試日誌。
