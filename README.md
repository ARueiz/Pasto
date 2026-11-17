# Pasto

多人即時共享剪貼簿。匿名建立房間，以六碼房碼、分享連結或 QR Code 加入，在會議室電腦、遠端電腦／跳板機與手機之間快速交換文字。

> 本專案同時用於學習 System Design、Scalability、Concurrency 與 AI-assisted Development。

## 目前狀態

**開發中，已有正式交易核心與建房／加入／身分恢復 API；metadata／關房 HTTP、文字 API、前端與即時同步尚未完成。**

| 項目 | 狀態 |
| --- | --- |
| 需求、架構、ADR | 已定案 |
| S1-01 PostgreSQL／EF Core 整合測試骨架 | 已完成（2 個 Creator 正反例） |
| S1-02 正式持久層骨架 | 已完成（正式 Migration；共 6 個整合測試通過） |
| S1-03 正式建房與 Owner／cascade | 已由 PR #2 合併至 main（1ad0e2b） |
| S1-04 併發交易核心 | 已由 GitHub PR #3 合併（2853cda）；實作方與審查方獨立實測均為 99 例通過 |
| S1-05 期限邊界 | 已由 PR #4 合併（781470a）；完整 120 例通過 |
| S2-01 建房 API | Claude 靜態審查無 P0～P2；日誌 P3 已修正為 58e7926，相關 39 例通過，未合併 |
| S2-02 加入／身分 | 審查後補齊真 Kestrel body 上限；完整 195 例通過，增量待 review；讀取配額風險依決策保留至 S7 處理 |
| S2-03 與後續 | 尚未開始；前端方向待討論 |

階段規劃見 [Sprint 1 實作計畫](docs/sprint-1-implementation-plan.md)。

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
