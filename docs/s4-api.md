# S4 前端 API 串接

前端位於 `src/Pasto.Client`。它以同源 `/api` 呼叫 S2／S3 的既有 REST 契約，開發時由 Vite proxy 導向 ASP.NET Core。完整的回應與錯誤契約見 [架構 §4](architecture.md)。

| 操作 | 請求 | S4 處理方式 |
| --- | --- | --- |
| 建房 | `POST /api/rooms` | 保存回應中的房間 UUID 與 Credential，開啟 `/r/:code`。 |
| 加入 | `POST /api/rooms/join`，`{ code }` | 用房碼取得此分頁的匿名身分。分享網址與 QR 都只包含房碼。 |
| 恢復身分 | `GET /api/rooms/{roomId}/me` | 房碼對應的 UUID 已有 Credential 時先驗證。401 顯示新身分加入選項，404 顯示房間失效。 |
| 讀取文字 | `GET /api/rooms/{roomId}/items` | 接收整份快照，依服務端排序反轉顯示。搜尋只取已載入的文字。 |
| 送出文字 | `POST /api/rooms/{roomId}/items`，`{ content }` | 送出原文，成功後讀取新快照。 |
| 刪除文字 | `DELETE /api/rooms/{roomId}/items/{itemId}` | `isOwn` 與 `canDelete` 為真時提供刪除，實際權限由後端判定。 |
| 關閉房間 | `DELETE /api/rooms/{roomId}` | 房主確認後呼叫，204 清除此房間的快照和憑證。 |

受保護的 API 使用 `Authorization: Bearer <credential>`。憑證以房間 UUID 存在 `sessionStorage`，另存房碼索引。主動離開只清除此房間在本分頁的資料。瀏覽器複製分頁或還原工作階段可能保留憑證副本。儲存被拒時，身分只保留在目前頁面的記憶體。過期的憑證與房間會清除資料並留下狀態標記，避免重新整理後自動加入。

429 顯示 `Retry-After`。寫入結果不確定時保留輸入並提示先取新快照，避免自動重送。其他裝置的更新由使用者手動重新整理。

本機執行方式：先依 [測試文件](testing.md#4-migration-與-host-的操作) 啟動後端並套用 Migration。後端使用 `http://127.0.0.1:5080`，若使用其他埠，在前端程序環境設定 `PASTO_API_TARGET`。

```powershell
cd src/Pasto.Client
npm ci
npm run dev
```

前端自動檢查使用 `npm run typecheck`、`npm run lint`、`npm run build`。`npm run test:ui` 以 Microsoft Edge 和模擬 API 驗證瀏覽器互動與錯誤狀態。

S4 交付驗證：TypeScript、Lint、Build 通過。Edge 6 例通過，檢查 360px 淺色畫面、1280px 深色畫面、主題切換、六種狀態、原文與 8 KiB 邊界、邀請網址加入和危險操作確認。後端 PostgreSQL 回歸 278 例通過（`s4-backend-regression.trx`）。瀏覽器案例使用模擬 API，尚未用實體手機掃碼或在瀏覽器直接連線測試正式後端。

`npm audit --omit=dev` 未發現漏洞。完整套件稽核仍標記建置及檢查工具共同使用的 `picomatch`，[公告 GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf)。在已解析的套件版本中沒有可安裝的修正版，開發伺服器僅供本機作業。
