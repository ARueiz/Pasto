# ADR 0004：可觀測性與自架部署

- 狀態　Accepted
- 日期　2026-11-20
- 實作順序與驗收見 [Sprint 1 實作計畫](../sprint-1-implementation-plan.md)。本 ADR 記錄決策，各階段的程式與基礎設施依計畫交付。

## 部署政策

Pasto 以自架 Docker Compose 為主要部署形式。AWS 是正式環境的部署選項。自架的基本功能可獨立運作，Seq、AWS 與外部 SaaS 監控皆屬選配。

**任何不受信任的裝置或使用者能夠連線的環境，都視為公開部署。** 內網有訪客 Wi-Fi、BYOD 或其他不受信任的來源可存取時，也適用公開部署規則。內網自架仍須保留驗證、限流與敏感資料保護。**S7 防護完成前不得公開部署。**

## 選擇與取捨

| 主題 | 決策 | 替代方案與取捨 |
| --- | --- | --- |
| 部署形式 | 自架 Compose。正式環境檔案放在 `deploy/`，保留根目錄測試用 Compose。 | AWS 優先或共用單一 Compose 檔。採自架可涵蓋內網及 NAS，需自行管理主機、TLS 與資料卷。獨立設定讓部署預設值與測試隔離，需各自驗證。 |
| 靜態檔 | ASP.NET Core 提供 Vite 建置產物，前端、REST 及 SignalR 同源。開發保留 Vite。 | 獨立靜態站或 CloudFront＋S3 可分開發布與快取，但增加同源、代理及環境配置成本。同源方案讓應用映像一起更新前後端。 |
| TLS | 內網預設 Caddy Internal CA，支援自備憑證及日後更換 TLS 終止方式。AWS 正式部署使用公開信任的憑證。 | 全部由使用者自備憑證會增加內網啟動門檻。Internal CA 需持久化並在各裝置信任根憑證。 |
| Schema | 獨立的一次性 Migration Job，DDL 與 API 帳號分離。API 啟動只檢查一次相容性，確認不相容時記 Critical、非零退出。DB 暫時不可達時有限等待。 | API 自動遷移會讓執行帳號需要 DDL 權限，也增加多程序啟動競爭。手動遷移降低自動化程度，容易漏步驟。獨立 Job 需處理啟動依賴與升級驗證。 |
| 應用程式日誌 | 保留 `ILogger<T>`，以 Serilog 輸出 Console。Docker driver 負責輪替。 | 內建 Console 相依較少。Serilog 提供一致結構與選配 sink，但需重新驗證框架日誌過濾及測試 provider。應用程式的檔案輸出會增加路徑權限和輪替責任。 |
| 健康狀態 | ASP.NET Core Health Checks 分開回報存活與 DB 就緒，清理 Worker 狀態另外回報。 | 單一檢查實作較少，但 DB 或 Worker 故障會混淆探針用途。分開後需配置內部路由與探針。 |
| Metrics 與 Traces | OpenTelemetry 採內建 meter 優先，OTLP 匯出依設定啟用；日誌繼續由 Serilog 處理。 | 廠商專用 SDK 或自訂全部指標會增加耦合及重複計數風險。OTel 仍需管理採樣、欄位遮蔽及接收端。 |
| 選配收集 | 自架可選 Seq；AWS 由基礎設施將 stdout／stderr 收到 CloudWatch。 | 將 Seq 或 CloudWatch SDK 設成必備會提高部署門檻。選配可維持單純的 Console 路徑，保留期限及警報由各環境管理。 |

## 安全界線

六碼房碼、Credential、使用者文字、Authorization、Cookie 及資料庫密碼須保護。RoomId 為無授權能力的內部 UUID，可用於日誌關聯。所有收集層都須驗證 URL 與診斷欄位已去除房碼與 `access_token`，低基數路由模板可保留。例外記錄沿用型別與堆疊框架等受控欄位。

## 階段依賴

S6 → Logging → Health Checks → OpenTelemetry → S7 → Deployment → S8 → Seq／AWS。

OpenTelemetry 提供 S8 量測所需的指標。S7 先處理已知的 admission 與 Join 濫用風險。Deployment 提供 S8 所需的近正式容器環境。Seq 與 AWS 各自選配。
