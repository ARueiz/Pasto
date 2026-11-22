# Pasto — 開發進度與下一步

更新日期：2026-11-23（Asia/Taipei）。這是目前狀態的入口。開工前先讀 [AGENTS.md](AGENTS.md)，並以實際 Git 狀態核對本檔。詳細設計與驗證紀錄以連結文件為準。

## 目前位置

- `main` 已包含 S1～S7、可觀測性與 Deployment。最近一次合併是 [PR #13](https://github.com/ARueiz/Pasto/pull/13)（Deployment）。
- 下一階段是 S8，尚未建立工作分支。
- 開工前以 `git status` 確認工作目錄狀態，不覆蓋未提交的修改。不得自行推送或合併，除非使用者明確授權。

## 階段進度

- [x] S1：正式持久層、交易核心、併發與期限邊界。[PR #1](https://github.com/ARueiz/Pasto/pull/1)～[#4](https://github.com/ARueiz/Pasto/pull/4)
- [x] S2：建房、Join、Bearer、me、metadata、關房 API。[PR #5](https://github.com/ARueiz/Pasto/pull/5)
- [x] S3：文字新增／刪除 API、整份快照、寫入配額。新增與刪除配額分開，DELETE room 與刪除文字共用刪除配額。[PR #6](https://github.com/ARueiz/Pasto/pull/6) · [S3 工作項](docs/s3-items.md)
- [x] S4：房間身分與分享、聊天室文字操作、搜尋、狀態提示、手動刷新與主題切換。[PR #7](https://github.com/ARueiz/Pasto/pull/7) · [UI 規格](docs/ui-spec.md)
- [x] S5：同房 SignalR 訂閱、提交後通知、整份快照同步、斷線重連與 REST 備援，以及作者代碼、訊息分組與智慧捲動。[PR #8](https://github.com/ARueiz/Pasto/pull/8) · [審查紀錄](docs/s5-review.md)
- [x] S6：到期／關房資料分批清理、房碼冷卻回收與 SignalR 連線清理。[PR #9](https://github.com/ARueiz/Pasto/pull/9)
- [x] 可觀測性：結構化日誌與 Hub 例外、存活／DB 就緒／Worker 狀態健康檢查、OpenTelemetry 指標與 Tracing、選配 OTLP。[PR #10](https://github.com/ARueiz/Pasto/pull/10)
- [x] S7：防護、資源與濫用處理。[PR #11](https://github.com/ARueiz/Pasto/pull/11)
  - [x] S7-1 憑證查詢防護：查無憑證才扣來源失敗額度，REST 與 SignalR 共用 DB 保護底線，跨房 REST 請求扣 Participant 讀取額度。
  - [x] S7-2 公開加入與猜碼防護：重犯退避、冷卻表滿載與同 NAT 加入。
  - [x] S7-3 SignalR 常駐連線配額、negotiate 速率與 `SubscribeRoom` 限流。WebSocket 強制使用協商取得的 token，並驗證 Origin。
  - [x] S7-4 HTTP 並行、過載 admission 與 partition 上限。
- [x] Deployment：同源靜態檔、Caddy TLS、可信代理、雙層網路、Migration Job 與自架 Compose。Linux Docker Engine 的來源 IP 與每來源限流已驗收。[PR #13](https://github.com/ARueiz/Pasto/pull/13) · [自架指南](deploy/README.md)
- [ ] S8：壓測與收尾。
- [ ] Seq／AWS：選配監控與 AWS 部署建議。

## 驗證紀錄

各階段的測試數量、命令、TRX 與失敗紀錄都在 [測試紀錄](docs/testing.md)，最新一筆是 [Deployment](docs/testing.md#deployment2026-11-22)。本檔不重複記錄。

目前不需要使用者額外手動測 API。若後續需要，應提供具體步驟與預期結果。

## 下一個工作

S8 壓測與文件收尾，涵蓋 1,000 條連線、同一 NAT、熱房、大量重連與過期清理。來源 IP 相關情境必須在 Linux Docker Engine 上執行，Docker Desktop 會讓所有用戶端顯示為同一來源。Ubuntu 測試虛擬機已保留供 S8 使用。 [S8 實作計畫](docs/sprint-1-implementation-plan.md#13-s8負載證據驗收與文件收尾) · [部署驗收紀錄](docs/testing.md#deployment2026-11-22)

## 後續必須保留的事項

- S6 第二項非阻擋 P3：`6009 RoomConnectionTrackingFull` 目前隨清理 Worker 彙總。停用 Worker 時沒有摘要。後續確認是否需要與 Worker 狀態獨立的有界摘要，保留每期間最多一筆的日誌節流。
- S7 已驗證全站 SignalR 連線配額及每個 Participant 3 條連線。S6 的 1,500 條上限只限制追蹤表記憶體，滿載仍可訂閱。正式反向代理的 token 遮蔽已在 Deployment 驗收。 [部署驗收紀錄](docs/testing.md#deployment2026-11-22)
- 後續可評估作者代碼撞碼處理。目前接受五碼相同的作者在連續顯示時可能被分在同組。
- S7／S8：同 NAT 共用失敗額度、跨房請求扣自身讀取額度。大量 /64 或在途併發仍可耗盡全站 DB 保護底線。查無快取與來源失敗桶已限量，既有框架 partition 的記憶體峰值與重連尖峰留待 S8 量測。 [第一批風險](docs/s2-api-identity.md#s7-第一批失敗計費)
- S7-4：DB 底線、建房及 Join 的來源速率桶共 10,000，已驗證 negotiate 的來源表 5,000，滿載時表外來源走各自的全站桶。Participant 表上限 50,000 個 ID，滿載時新身分沿用適用的 DB 底線、每房配額與 in-flight，並記錄節流警告。
- S7-4／S8：快照寫出前已釋放 HTTP 處理名額，慢讀仍可能占用記憶體。S8 量測約 14.8 MB 最大回應情境的程序與代理記憶體峰值。
- S7：持有分享連結者可反覆 Join 耗盡 1,000 個生命週期 Participant 名額。
- S7-2／S8：攻擊者以大量 /64 輪流送錯碼，可在恢復期反覆觸發壓力，使全站長期處於壓力模式。壓力期間若攻擊者持續送出每秒 2 次以上的請求，大部分合法 Join 會被拒，使用者需要重試。S8 量測壓力模式下合法 Join 的成功率與延遲。
- S7／S8：實測以控制字元填滿 300 筆時的 JSON 回應大小與讀取頻寬。S8 另測 1,000 條連線同時重抓。
- **公開部署規則**：任何不受信任的裝置或使用者可連線的環境都算公開部署，包括可由訪客 Wi-Fi 或 BYOD 存取的內網。受信任內網仍需驗證、限流與敏感資料保護。S7 已完成，公開部署前仍應確認上列已知風險可接受。 [ADR 0004](docs/decisions/0004-observability-and-deployment.md)
- S4 P3：在選單按 Esc，或用 Esc 取消確認對話框之後，焦點會掉到 `<body>`。建議把焦點還給「更多選項」按鈕。
- S4 P3：浮動複製提示的垂直位置寫死在 128px（`4 * --space-8`）。放大字級或展開 QR 面板時，提示會疊在其他元素上。它設了 `pointer-events: none`，不會擋到點擊。
- S4：iPhone 掃 QR Code 加入尚未實機測試。
- Deployment 以自架 Compose 與 ASP.NET Core 同源靜態檔為基準。CSP、WSS、安全 header、快取與代理驗收見 [Deployment 計畫](docs/sprint-1-implementation-plan.md#12-deployment)。

## 文件入口與維護

- [AGENTS.md](AGENTS.md)：長期開發規範。
- [README](README.md)：專案介紹、架構、快速開始與各階段交付的 PR。
- [測試紀錄](docs/testing.md)：測試數量、命令、實測證據、失敗與環境資訊。
- [自架部署指南](deploy/README.md)：部署設定與驗收步驟。
- [分階段計畫](docs/sprint-1-implementation-plan.md)、[架構](docs/architecture.md)、[需求](docs/requirements.md)、[ADR 0003](docs/decisions/0003-mvp-scope-reduction.md)、[ADR 0004](docs/decisions/0004-observability-and-deployment.md)：詳細設計與既定取捨。

每個 patch 提交、review 結果或合併完成後更新本檔；保留目前狀態與下一步，歷史細節移至對應文件。測試數量與 commit hash 只記在 [測試紀錄](docs/testing.md)，本檔與 README 不重複記錄。
