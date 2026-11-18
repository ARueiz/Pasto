# Pasto UI 規格（S4）

> 視覺參考：`docs/design/pasto-ui-draft.html`（可互動草稿，只當參考，不再維護；畫面以程式碼為準）
> 色彩與尺寸：`src/Pasto.Client/src/styles/tokens.css`
> 品牌資產：`src/Pasto.Client/public/`（favicon、App 圖示）、`src/Pasto.Client/src/assets/logo/`（SVG）
> API 串接：[S4 前端 API](s4-api.md)

---

## 1. 色彩系統

| 用途 | Token | Light | Dark | 說明 |
|---|---|---|---|---|
| Primary | `--accent` | `#0D9488` | `#14B8A6` | 品牌色，只用在圖示、邊線、焦點框等非文字元素 |
| Primary Strong | `--accent-strong` | `#0F766E` | `#14B8A6` | 按鈕底色、自己的訊息泡泡、主色文字 |
| Primary Hover | `--accent-hover` | `#115E59` | `#2DD4BF` | 按鈕滑過 |
| Primary Soft | `--accent-soft` | `#E0F2F0` | `#123634` | 徽章、圖示底、次要按鈕 |
| On Primary | `--accent-fg` | `#FFFFFF` | `#042F2E` | 放在 Primary Strong 上的文字 |
| Background | `--bg` | `#F8FAFC` | `#0B1220` | 頁面底色 |
| Surface | `--surface` | `#FFFFFF` | `#172532` | 卡片、導覽列、他人泡泡 |
| Surface 2 | `--surface-2` | `#F1F5F9` | `#1E3040` | 次要底色、hover |
| Text | `--fg` | `#152D31` | `#F1F5F9` | 主要文字 |
| Text Muted | `--fg-muted` | `#4A6166` | `#9DAEB9` | 時間、說明 |
| Border | `--border` | `#E2E8F0` | `#304250` | 邊框、分隔線 |
| Danger | `--danger` | `#C62828` | `#F47C7C` | 離開、刪除、關閉房間、錯誤 |
| Highlight | `--highlight` | `#FDE68A` | `#FDE68A` | 搜尋關鍵字標記 |

**對比檢查（WCAG AA）**：所有文字組合 ≥ 4.5:1，非文字元素 ≥ 3:1，淺色與深色模式皆通過。

| 組合 | Light | Dark |
|---|---|---|
| 主要文字／背景 | 13.83:1 | 17.09:1 |
| 次要文字／卡片 | 6.58:1 | 6.82:1 |
| 按鈕文字／Primary Strong | 5.47:1 | 5.81:1 |
| 主色文字／卡片 | 5.47:1 | 6.26:1 |
| 品牌色／卡片（非文字） | 3.74:1 | 6.26:1 |
| 危險色／卡片 | 5.62:1 | 5.95:1 |

**主題切換**：預設跟隨系統；使用者可在 ⋯ 選單切換。選擇存在 `localStorage`（key：`pasto-theme`，值 `light` / `dark`，跟隨系統時刪除 key），套用方式是在 `<html>` 設定 `data-theme`。

---

## 2. 字體與尺寸

- 字體：Inter + Noto Sans TC；房碼、byte 計數用 JetBrains Mono。
- 字級：內文 15px、按鈕 14px、時間與說明 12px、標籤 11px、房碼 18px（`letter-spacing: .14em`）。
- 圓角：按鈕 8px、卡片 12px、輸入列 14px、泡泡 16px（靠近發送者那一角 4px）。
- 桌面版訊息欄最寬 760px，泡泡最寬為訊息欄的 78%（上限 520px）。

---

## 3. 頁面

### 3.1 入口頁 `/`

- 標題「在裝置之間傳文字」，副標說明房碼與 QR、不用註冊。
- 兩張卡片（手機上下排、桌面左右排）：
  - **建立房間**：說明「你會成為房主，可以分享房碼，也可以提前關閉房間。」，主按鈕「建立新房間」。
  - **加入房間**：六碼輸入框（等寬字、自動轉大寫、`maxlength=6`）＋「加入」按鈕。
- 頁尾提示：「房間裡的文字任何知道房碼的人都看得到，請不要放密碼等機密資料。」
- 掃 QR 或開分享連結 `/r/:code` 的使用者直接進房，不經過入口頁。

### 3.2 房間頁 `/r/:code`（聊天室版面）

由上到下：

1. **Header**：logo＋字標「Pasto.」與標語「Share instantly.」；右側「N 人在線」、搜尋按鈕、⋯ 選單。
2. **房間列**：「目前房間」＋房碼；右側 QR 按鈕（展開／收合 QR 面板）、「複製邀請」按鈕。
3. **QR 面板**（預設收合）：QR Code＋分享連結＋說明「QR 只包含加入連結，掃描的裝置會取得自己的新身分。」
4. **訊息列表**：
   - 由舊到新排列，進房時捲到最底。
   - 依時間插入日期分隔（例：「今天 19:16」）。
   - **他人**：靠左、`--surface` 底、有框線、上方統一顯示「訪客」。
   - **自己**：靠右、`--accent-strong` 底、`--accent-fg` 字、上方顯示「你」。
   - 每則下方：時間＋「複製」；自己的訊息多一個「刪除」。
   - 文字保留原始空白與換行（`white-space: pre-wrap`），網址**不轉成連結**。
   - S4 列表底部顯示「其他裝置的新訊息不會自動出現」＋「重新整理」。
5. **輸入列**（固定在底部）：自動長高的 textarea（最高約 5 行）＋送出按鈕。
   - 有輸入時在右下顯示 byte 計數 `n / 8,192 bytes`（UTF-8 計算）。
   - 空白、只有空白字元、超過 8,192 bytes 時送出按鈕停用；超過時計數變紅並顯示「超過上限」。

**⋯ 選單**：切換深淺色、重新整理。分隔線後，房主只看到「關閉房間」（標示「房主」），訪客只看到「離開房間」。MVP 沒有房主權限轉移，房主清除分頁身分後會永久失去關房權限，因此房主選單不提供離開操作。

**複製回饋**：成功時顯示約兩秒的浮動提示「已複製邀請」或「已複製文字」。提示不改變版面，也不遮住輸入框。剪貼簿 API 不可用或拒絕時，顯示第 5 節的手動複製框。

**搜尋**：

- 點放大鏡後，房間列換成搜尋框＋「取消」。
- 即時篩選：只顯示含關鍵字的訊息（不分大小寫），關鍵字用 `<mark>` 標記，搜尋框內顯示「N 筆」。
- 搜尋時隱藏日期分隔與同步提示；找不到時顯示「找不到符合的文字／只會搜尋這個房間目前載入的內容。」
- 「取消」或 Esc 關閉搜尋，清空關鍵字並捲回最底。
- 標記要用文字節點組合（或先 escape），不可把使用者文字直接塞進 `innerHTML`。

---

## 4. 元件清單

| 元件 | 說明 |
|---|---|
| `Logo` | `layers` 疊層圖示 |
| `RoomHeader` | Logo、在線人數、搜尋按鈕、⋯ 選單 |
| `InviteBar` | 房碼、QR 按鈕、複製邀請 |
| `QrPanel` | QR Code 與分享連結 |
| `SearchBar` | 搜尋框、結果數、取消 |
| `MessageList` | 日期分隔、同步提示、空狀態、搜尋結果 |
| `MessageBubble` | `variant: "mine" \| "other"`，含複製／刪除 |
| `Composer` | 自動長高輸入框、byte 計數、送出 |
| `RoomMenu` | ⋯ 下拉選單 |
| `ConfirmDialog` | 頁面內確認，用於離開、關閉房間、刪除 |
| `StatusNotice` | 錯誤、資訊類提示（見第 5 節） |
| `Button` | `primary`（Strong 底）、`ghost`（框線）、`soft`（淡底）、`danger`（紅框） |

---

## 5. 狀態提示文案

| 情境 | 標題 | 內文 | 動作 |
|---|---|---|---|
| `/me` 回 401（身分失效） | 這個分頁的身分已失效 | 原本的刪除權和房主權無法恢復。要用新身分重新加入 {code} 嗎？ | 以新身分加入 |
| 404（房間關閉或到期） | 這個房間已關閉或到期 | 房間裡的文字已無法讀取。可以回到首頁建立新房間。 | 回首頁 |
| sessionStorage 無法使用 | 身分只保存在這次開啟的頁面 | 瀏覽器不允許保存身分，重新整理後會失去目前身分。 | — |
| 剪貼簿 API 被拒 | 無法自動複製 | 文字已幫你選取，請按 Ctrl＋C 或長按複製。 | — |
| 429（請求太頻繁） | 操作太頻繁 | 請在 {n} 秒後再試一次。 | — |
| S4 手動刷新 | 其他裝置的新內容不會自動出現 | 按「重新整理」取得最新內容。 | 重新整理 |

---

## 6. 決策紀錄

| # | 項目 | 決定 |
|---|---|---|
| 1 | 淺色主色按鈕對比 | 按鈕、自己的泡泡、主色文字改用 `#0F766E`；`#0D9488` 只用於非文字裝飾。按鈕滑過色往深一階到 `#115E59`。 |
| 2 | 「N 人在線」 | S4 先寫死顯示；S5 串 SignalR 取得在線人數。 |
| 3 | 網址顯示 | 維持純文字，不轉成連結，只提供複製。 |
| 4 | 搜尋 | S4 只在前端比對已載入的訊息，不新增後端 API；之後若支援分頁或長歷史再評估後端搜尋。 |
| 5 | 自己的泡泡底色 | 同第 1 項，使用 `#0F766E`。 |
| 6 | 主題偏好存在 localStorage | 列為 S4 例外：只存 `light` / `dark`，不存憑證或文字。 |
| 7 | 危險操作確認 | 離開、關閉房間、刪除都在頁面內確認，不使用瀏覽器原生 `confirm()`。 |
| 8 | 品牌與訪客名稱 | 使用 `layers` 疊層圖示。快照沒有其他參與者的識別資料，對方訊息統一顯示「訪客」。 |

---

## 7. 品牌資產

| 檔案 | 用途 |
|---|---|
| `src/assets/logo/<name>-mark.svg` | 單色圖形（`currentColor`），放在 Header 或任意底色上 |
| `src/assets/logo/<name>-icon.svg` | 圓角方塊＋圖形，App 圖示原稿 |
| `public/favicon.svg` | 現代瀏覽器 favicon（支援深淺色） |
| `public/favicon.ico` | 16／32／48px，舊瀏覽器 |
| `public/apple-touch-icon.png` | 180px，iOS 加到主畫面 |
| `public/icon-192.png`、`public/icon-512.png` | PWA manifest |

`index.html` 引用：

```html
<link rel="icon" href="/favicon.ico" sizes="48x48">
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<meta name="theme-color" content="#F8FAFC" media="(prefers-color-scheme: light)">
<meta name="theme-color" content="#0B1220" media="(prefers-color-scheme: dark)">
```

字標「Pasto.」直接用 CSS 文字呈現（Inter 800、`letter-spacing: -.02em`），不輸出成圖檔。
