# Pasto

多人即時共享剪貼簿。匿名建立房間，以六碼房碼、分享連結或 QR Code 加入，在會議室電腦、遠端電腦／跳板機與手機之間快速交換文字。

> 本專案同時用於學習 System Design、Scalability、Concurrency 與 AI-assisted Development。

## 目前狀態

**S1～S4 已合併至 `main`。** 目前可在瀏覽器建房、分享與操作文字。跨裝置更新需手動重新整理，下一階段是 S5 的 SignalR 通知與自動同步。

| 項目 | 狀態 |
| --- | --- |
| 需求、架構、ADR | 已定案 |
| S1-01 PostgreSQL／EF Core 整合測試骨架 | 已完成（2 個 Creator 正反例） |
| S1-02 正式持久層骨架 | 已完成（正式 Migration；共 6 個整合測試通過） |
| S1-03 正式建房與 Owner／cascade | 已由 PR #2 合併至 main（1ad0e2b） |
| S1-04 併發交易核心 | 已由 GitHub PR #3 合併（2853cda）；實作方與審查方獨立實測均為 99 例通過 |
| S1-05 期限邊界 | 已由 PR #4 合併（781470a）；完整 120 例通過 |
| S2 房間／匿名身分 API | 已由 PR #5 合併（9eace5f）；228 例通過，使用者確認測試審查 OK |
| S3 文字操作、配額與快照 | 已由 [PR #6](https://github.com/ARueiz/Pasto/pull/6) 合併（`dbfcb24`）。獨立審查的兩輪完整測試各 278 例通過。見 [S3 工作項](docs/s3-items.md) |
| S4 基本前端 | 已由 [PR #7](https://github.com/ARueiz/Pasto/pull/7) 合併（`00c2183`）。包含入口頁、房間頁、身分恢復、分享、文字操作、搜尋與手動刷新。Edge 10 例通過。見 [UI 規格](docs/ui-spec.md) 與 [本機啟動與 API](docs/s4-api.md) |
| S5 SignalR 通知與同步 | 下一階段，尚未開始 |

目前交付點與下一步見 [TODO.md](TODO.md)，整體規劃見 [Sprint 1 實作計畫](docs/sprint-1-implementation-plan.md)。

## MVP 範圍

- 匿名建房，不需註冊或登入；房間建立後固定 5 小時失效
- 六碼手動加入、分享 URL／QR Code
- 多筆純文字：新增、複製、刪除自己的文字（每筆 8 KiB、每房 300 筆）
- 房主可提前關閉整個房間
- SignalR 即時通知，REST 重新同步

不含檔案上傳、文字編輯、帳號系統與公開房間列表。完整範圍見 [需求規格](docs/requirements.md)。

## Tech Stack

- Frontend：React + TypeScript + Vite
- Backend：ASP.NET Core (.NET 10)、SignalR
- Database：PostgreSQL + EF Core
- Infrastructure：Docker Compose
- Architecture：Modular Monolith（API／Application／Domain／Infrastructure）

## 專案結構

```text
Pasto/
├─ AGENTS.md                    AI Agent 協作規範
├─ global.json                  固定 .NET SDK 版本
├─ Pasto.slnx                   後端與測試的 Solution
├─ dotnet-tools.json            本機 dotnet-ef 工具版本
├─ compose.yaml                 本機 PostgreSQL
├─ src/Pasto.Server/            Api／Application／Domain／Infrastructure
├─ docs/
│  ├─ requirements.md           需求規格
│  ├─ architecture.md           架構設計
│  ├─ decisions/                ADR（架構決策紀錄）
│  ├─ sprint-1-implementation-plan.md
│  ├─ testing.md                整合測試執行方式
│  └─ s1-01-dotnet-postgresql-guide.md
└─ tests/
   └─ Pasto.IntegrationTests/   真 PostgreSQL 整合測試
```

## 執行整合測試

需要 .NET SDK 10.0.402 與本機 PostgreSQL。完整步驟（含 Docker 啟動資料庫）見 [docs/testing.md](docs/testing.md)。

```powershell
$env:PASTO_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55432;Database=pasto_test;Username=pasto_test;Password=<本機測試密碼>'
dotnet tool restore
dotnet restore Pasto.slnx --locked-mode
dotnet test Pasto.slnx --configuration Release --no-restore
```

測試只接受 loopback 主機，且資料庫名稱須以 `_test` 結尾，避免誤連正式資料庫。

## 文件

- [需求規格](docs/requirements.md)
- [架構設計](docs/architecture.md)
- [ADR 0002：六碼房碼與匿名參與者](docs/decisions/0002-room-code-and-anonymous-participants.md)
- [ADR 0003：MVP 範圍收斂](docs/decisions/0003-mvp-scope-reduction.md)
- [S1-01 入門導讀](docs/s1-01-dotnet-postgresql-guide.md)
- [S1-02 完成報告與維護導讀](docs/s1-02-maintenance-report.md)
- [S1-03 建房流程與驗證](docs/s1-03-room-creation.md)
- [S1-04 交易拆分與 Patch 1 驗證](docs/s1-04-item-transactions.md)
- [S1-05 期限邊界與 S1 驗證對照](docs/s1-05-expiry-boundaries.md)
- [S2 API／匿名身分工作項與 S2-01 設定](docs/s2-api-identity.md)
