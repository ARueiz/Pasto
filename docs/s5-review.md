# Pasto S5 審查交接與結果

2026-11-19（Asia/Taipei）初次整理，適用於本機工作目錄 `C:\Projects\Pasto`。審查規範見 [AGENTS.md](../AGENTS.md) 與 [CLAUDE.md](../CLAUDE.md)。

## 2026-11-20 審查結果

- 審查方回報無阻擋問題。跨午夜作者標籤 P3 已修正，平滑捲動中斷已補 Edge E2E。11/20 獨立驗證涵蓋 Release Build、格式檢查與修整後的前端檢查，合併前驗證亦回報 PostgreSQL 整合測試 284／284 通過。 [合併前驗證](testing.md#s5-合併前修整驗證2026-11-20)
- 雙 Edge 搭配真後端的實測為 Codex 的紀錄，審查方尚未獨立重跑。到期連線清理留在 S6，Hub 配額、token 日誌及正式反向代理留在 S7，負載驗證留在 S8。
- 本文件隨 S5 一起保留審查脈絡與驗證入口。合併至 `main` 後依 AGENTS.md §10 同步 README 與 TODO 的進度。 [TODO](../TODO.md)

## 審查範圍

- 分支為 `feature/s5-signalr-sync`，基準為 `efbcb73`，包含 S4 合併點 `00c2183`。交接時 S5 位於未提交的工作目錄。
- 審查包含既有檔案的差異及下列六個新增的程式與測試檔。`pasto-design/` 是既有的本機設計參考目錄，作為版面脈絡即可。
- 本輪以審查報告為交付物。程式修正、提交、推送、合併與公開部署由使用者另行決定。

```powershell
git status --short --branch
git diff efbcb73 -- src tests
git ls-files --others --exclude-standard -- src tests
git diff --check
```

新增檔案請直接閱讀，`git diff efbcb73` 的輸出不會列出它們的內容。

- [useRoomScroll.ts](../src/Pasto.Client/src/useRoomScroll.ts)
- [useRoomSync.ts](../src/Pasto.Client/src/useRoomSync.ts)
- [ParticipantAuthorCode.cs](../src/Pasto.Server/Api/ParticipantAuthorCode.cs)
- [RoomChangeNotifications.cs](../src/Pasto.Server/Api/RoomChangeNotifications.cs)
- [RoomHub.cs](../src/Pasto.Server/Api/RoomHub.cs)
- [RoomHubTests.cs](../tests/Pasto.IntegrationTests/RoomHubTests.cs)

## 交付與審查重點

| 範圍 | 預期行為 | 優先閱讀 |
| --- | --- | --- |
| Hub 授權與傳輸 | `/hubs/room` 驗證 Participant 與房間，加入內部 UUID 群組後複驗。檢查複驗失敗時的移除、關房競爭、Origin 驗證與瀏覽器傳輸憑證的處理範圍。 | [RoomHub.cs](../src/Pasto.Server/Api/RoomHub.cs)、[ParticipantAuthenticationHandler.cs](../src/Pasto.Server/Api/ParticipantAuthenticationHandler.cs)、[Program.cs](../src/Pasto.Server/Program.cs)、[vite.config.ts](../src/Pasto.Client/vite.config.ts) |
| 提交後通知 | 新增、實際刪除及關房成功提交後，向同房群組發送僅含 `roomId`／十進位版本字串的 `RoomChanged`。通知期限為 2 秒，交易失敗時無通知，通知失敗後仍可透過 REST 讀到已提交資料。 | [ItemEndpoints.cs](../src/Pasto.Server/Api/ItemEndpoints.cs)、[RoomEndpoints.cs](../src/Pasto.Server/Api/RoomEndpoints.cs)、[RoomChangeNotifications.cs](../src/Pasto.Server/Api/RoomChangeNotifications.cs) |
| REST 同步與寫入 | Hub 可用時先訂閱再讀快照。Hub 失敗時仍可載入、手動重新整理及前景補查。檢查重複與亂序通知合併、單一進行中的同步請求、版本不倒退、重試退避、切房後舊 401／404 的隔離，以及重連後的補讀。寫入由 REST 授權，本機以 `pending` 防止重複操作。首次協商期間的 REST 讀取成功應保留「連線中」。 | [useRoomSync.ts](../src/Pasto.Client/src/useRoomSync.ts)、[api.ts](../src/Pasto.Client/src/api.ts)、[Room.tsx](../src/Pasto.Client/src/Room.tsx) |
| 捲動及分組 | 貼底時跟隨，向上閱讀時保留位置，離底部超過 80px 顯示「跳到最新」。平滑捲動期間的快照應更新終點，使用者可中斷。自己送出時應跳到提交版本，包含快照先於 POST 回應抵達的情境。同作者在顯示清單內相鄰、相隔至多五分鐘且中間無日期分隔線才分組。搜尋後依結果重新判斷，每則訊息保有作者的可及名稱。 | [useRoomScroll.ts](../src/Pasto.Client/src/useRoomScroll.ts)、[Room.tsx](../src/Pasto.Client/src/Room.tsx)、[app.css](../src/Pasto.Client/src/styles/app.css) |
| 作者代碼及舊身分 | 建房、加入、`/me` 和現有房間快照共用五碼作者代碼。快照提供 `authorCode`、`authorIsCreator`，作者 Participant UUID 留在伺服器投影。自己的代碼常駐輸入框旁，房主標籤使用半形 `(房主)`。配色從代碼取六色，舊 session 透過 `/me` 補齊。 | [ParticipantAuthorCode.cs](../src/Pasto.Server/Api/ParticipantAuthorCode.cs)、[ItemSnapshotStore.cs](../src/Pasto.Server/Infrastructure/Persistence/ItemSnapshotStore.cs)、[ParticipantEndpoints.cs](../src/Pasto.Server/Api/ParticipantEndpoints.cs)、[RoomCreationEndpoint.cs](../src/Pasto.Server/Api/RoomCreationEndpoint.cs)、[App.tsx](../src/Pasto.Client/src/App.tsx)、[session.ts](../src/Pasto.Client/src/session.ts) |

需求與取捨以 [S5 原始驗收計畫](sprint-1-implementation-plan.md#6-s5signalr-與方案-a-同步)、[需求 AC-08／AC-09](requirements.md#6-驗收條件) 及 [目前架構與 API 契約](architecture.md) 為準。favicon 變更位於 [index.html](../src/Pasto.Client/index.html)，使用帶版本的 Pasto SVG／ICO 網址。

## 驗證證據

| 驗證 | 執行者與結果 |
| --- | --- |
| .NET Release Build、dotnet format | Claude 於 11/20 重跑通過，零警告、零錯誤。 |
| 真 PostgreSQL 整合測試 | 11/20 合併前驗證回報 **284／284 通過**。另已核對本機 11/19 的 `tests/Pasto.IntegrationTests/TestResults/s5-round3.trx`，284 通過、0 失敗、0 略過。固定 SHA-256 向量為 `0653C`。見 [RoomHubTests.cs](../tests/Pasto.IntegrationTests/RoomHubTests.cs) 與 [ItemSnapshotApiTests.cs](../tests/Pasto.IntegrationTests/ItemSnapshotApiTests.cs)。 |
| 前端 Lint、Build、Edge E2E | Claude 於 11/20 修整後重跑，Lint 零警告，Build（含 `tsc --noEmit`）通過，Edge **28 通過、0 失敗**，耗時 **29.6 秒**。第 7 例跨午夜作者標籤及第 18 例鍵盤中斷平滑捲動均通過，與 Codex 實作方驗證一致。見 [clipboard.spec.ts](../src/Pasto.Client/e2e/clipboard.spec.ts)。 |
| 兩個 Edge 工作階段搭真後端 | Codex 以隔離 PostgreSQL schema 驗證房主與訪客標籤、訪客代碼跨畫面一致、重新整理恢復。較早的 S5 實測中，故意丟棄通知後，前景頁面兩次分別在 17.15 秒和 18.68 秒補回資料。 |
| 本機舊版後端排查 | 實際曾看到新版前端搭配舊 Debug 後端，房主代碼顯示 `undefined`、訪客代碼空白。重新建置並啟動新版後端於 `127.0.0.1:5080` 後，使用既有 Vite `localhost:5173` 和兩個 Edge 工作階段重整房間，已確認雙方顯示五碼，房主帶 `(房主)`。後續驗證使用同一版前後端。 |

完整過程、失敗紀錄與目前的測試邊界見 [測試紀錄](testing.md)。

## 可重跑的驗證

後端測試環境與 `PASTO_TEST_CONNECTION_STRING` 設定見 [README](../README.md) 與 [測試環境說明](testing.md)。整合測試使用本機專用 `_test` 資料庫及隔離 schema。

```powershell
dotnet build Pasto.slnx -c Release
dotnet format Pasto.slnx --verify-no-changes --no-restore
dotnet test Pasto.slnx -c Release --no-build --no-restore
```

```powershell
cd src/Pasto.Client
npm run lint
npm run build
npm run test:ui -- --workers=1
```

手動驗證時，在兩個獨立瀏覽器工作階段進同一測試房。先確認雙方新增與刪除能同步，再只封鎖訪客端的 `*/hubs/room/negotiate*` 並重載，檢查快照、手動更新、前景補查及 REST 寫入。解除封鎖後確認重連。作者顯示可用同一房間比較房主和訪客的輸入框代碼與對方看到的訊息標籤。

## 已知取捨與後續階段

- 五碼作者代碼容許撞碼，相同代碼的兩位訪客可能被分到同組。代碼作為顯示資訊，房間身分及操作權一律由伺服器驗證。 [架構說明](architecture.md)
- 關房／到期後的實體清理與連線清理留在 S6。公開部署前的資源保護與代理驗證在 S7，千條連線、熱房及最大快照的負載驗證在 S8。 [後續工作](../TODO.md#後續必須保留的事項)
- 驗證表保留 Claude 的獨立重跑結果、合併前驗證回報及 Codex 實測的來源。審查報告格式見 [CLAUDE.md](../CLAUDE.md#7-review-output-format)。
