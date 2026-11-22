# Pasto 自架部署

在 `deploy/` 執行指令。正式自架請使用 Linux 主機上的 Docker Engine 與 Docker Compose，並準備可用的 TCP 80、443。根目錄的 Compose 供本機開發與整合測試使用。

Docker Desktop 僅供試用。在本次 Windows 實測中，Caddy 看到的所有來源 IP 都是代理網路的閘道 `172.30.42.1`，使不同使用者共用每來源的限流額度。S8 涉及來源 IP、NAT 或每來源限流的壓測須在完成來源 IP 驗收的 Linux Docker Engine 上執行。 [Docker Desktop 網路架構](https://docs.docker.com/desktop/features/networking/)

## 初始化與啟動

1. 將 `.env.example` 複製為 `.env`，填入三組不同的強密碼。限制 `.env` 的檔案存取權。`PASTO_IMAGE` 使用可識別版本的本機 tag，每次升級換新 tag。
2. 設定 `PASTO_SITE_HOST`，填裝置實際使用的 DNS 名稱或 IPv4 位址，不含通訊協定、路徑和連接埠。若使用 DNS，讓電腦與手機都能解析到 Docker 主機。可用 `PASTO_BIND_ADDRESS` 指定主機的 IPv4 介面。
3. 確認 `PASTO_EDGE_SUBNET` 避開主機、NAS、VPN 與既有 Docker 網段。若調整子網，將 `PASTO_CADDY_IP` 和 `PASTO_APP_EDGE_IP` 一併改為子網內不同的可用 IP。App 只在自己的 `edge` IP 監聽 HTTP，且只信任 Caddy 的固定 IP。`db` 是內部網路，供 App、Migration Job 和 PostgreSQL 使用。
4. 預設 `PASTO_TLS=internal`。如已有憑證，將憑證鏈及私鑰放在 `certs/`，設定 `PASTO_TLS='/certs/site.crt /certs/site.key'`。憑證 SAN 須涵蓋實際的主機名稱或 IP。公開環境使用公開信任的憑證。

```sh
docker compose config --quiet
docker compose build migrate
docker compose up -d
docker compose ps -a
```

PostgreSQL 健康後，Migration Job 以 `pasto_migrator` 執行版本檢查、遷移與個別資料表授權，成功退出後 App 才啟動。兩者使用同一個映像，只有 Job 入口加上 `--migrate`。App 與 Cleanup Worker 使用 `pasto_runtime`。只有 Caddy 發布連接埠，健康端點可在代理網路內診斷。

### 建立裝置信任

首次使用 Internal CA 時匯出根憑證。透過可信通道傳給使用者，核對 SHA-256 指紋，並保留 Caddy 資料卷，以免重建容器後換掉 CA。

```sh
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ./certs/pasto-root.crt
```

- Windows：將根憑證匯入受信任的根憑證授權單位。macOS：用「鑰匙圈存取」匯入，並為該 CA 設定信任。
- iOS：安裝憑證描述檔後，在「設定 → 一般 → 關於本機 → 憑證信任設定」開啟該 CA 的完全信任。
- Android：在裝置的安全性或憑證設定安裝 CA 憑證。選單位置依版本與廠牌而異。以實際使用的瀏覽器驗證信任。

從電腦和手機開啟 `https://<PASTO_SITE_HOST>`，確認連線受到信任，實際驗證複製、貼上、房間分享與 WebSocket 更新。

## 升級、回復與保存

- 升級前備份 PostgreSQL，確認備份可還原。更新程式與 `PASTO_IMAGE`，執行 `docker compose build migrate`、`docker compose stop app`、`docker compose up -d`，檢查 Job 結束碼和 App 日誌。初次初始化腳本只在空 PostgreSQL 資料卷執行。更換既有密碼時，先在 DB 變更角色密碼，再更新 `.env` 並重建對應服務。
- App 啟動時使用唯讀查詢核對 migration 歷史。DB 連線暫時不可用時最多嘗試六次。缺少或存在未知 migration 時記錄 Critical 並以非零碼退出。回退至不相容的映像前，先還原相容備份，或在備份保護下執行已驗證的降版程序。
- App、Caddy 與 PostgreSQL 使用 `unless-stopped`。Docker 重啟可能先啟動 App，再啟動 PostgreSQL，App 會有限重試。schema 持續不相容時 Docker 會退避重啟，請查看 `docker compose logs app`。Migration Job 不會由 Docker 自動重啟。
- PostgreSQL 和 Caddy 資料保存在具名資料卷。房間及剪貼簿資料由既有到期清理規則處理。由管理員依部署政策設定 DB 備份保存期限，並定期清除過期備份。備份可能含有已從線上資料庫清除的文字。
- 容器日誌寫至 stdout／stderr。預設 `local` driver 每個容器最多五個輪替檔、每檔 10 MB。NAS 不支援 `local` 時，可設 `PASTO_LOG_DRIVER=json-file` 並重新建立容器。Caddy 存取及錯誤日誌移除 URI 與 HTTP 標頭欄位，保留狀態、耗時等營運資訊。

## 驗收

在隔離的測試主機與測試資料卷執行重啟及故障測試。

- 權限驗收使用下列 catalog 測試。另以實際房間流程驗證新增、讀取、更新、刪除及 Cleanup Worker。

  ```sh
  docker compose exec -T postgres psql -U pasto_admin -d pasto < check-db-privileges.sql
  ```

  PowerShell 可用 `Get-Content -Raw check-db-privileges.sql | docker compose exec -T postgres psql -U pasto_admin -d pasto`。

- 用 `docker compose images app migrate` 確認 App 與 Job 的映像 ID 相同，並用 `docker compose ps`、`docker network inspect` 核對網路成員和發布的連接埠。從一般容器網路驗證 App 和 PostgreSQL 均無法直連。從 `db` 測試容器驗證 PostgreSQL 可連、App 的 8080 不可連。從 Caddy 容器存取 `http://app:8080/health/ready` 應可成功，而公開 HTTPS 的 `/health/ready` 應回 404。
- 從測試用 `edge` 容器對 App 的 Hub negotiate 送出 HTTPS `Origin` 和 `X-Forwarded-Proto: https`，應回 403。用既有的來源限流測試確認 `X-Forwarded-For` 也只採用可信來源。再以信任部署 CA 的瀏覽器，從 Caddy 的 HTTPS 網址開房，確認 Network 面板出現成功的 WSS 升級，且另一個視窗可收到即時更新。
- 在正式 Linux 主機上，用兩台來源 IP 不同的裝置透過發布的 HTTPS 連接埠連線。確認 Caddy 的 `client_ip` 分別是兩台裝置的實際來源 IP，並在隔離驗收環境觸發其中一台的每來源額度，確認另一台仍能正常建房。完成這項驗收後再進行 S8 的來源 IP 相關情境。
- 驗證首頁和 `/r/{code}` 的 `Cache-Control: no-cache`、雜湊資產的 `immutable` 與長效快取、API 的 `no-store`。刷新房間路徑，確認不存在的 API、Hub、健康檢查或靜態檔仍是 404。檢查 CSP、HSTS、`Referrer-Policy` 和 `nosniff`。在 Caddy、App 日誌及啟用的 OTLP 接收端檢查房碼、token 和正文均未外洩。
- 同一映像的兩個 Migration Job 並行執行後，確認 migration 歷史只記錄一次、兩個 Job 均成功、權限驗收通過。以隔離 DB 驗證空白及較新 schema 會使 App 非零退出，並有明確的 Critical 日誌。重啟測試主機或 Docker daemon，確認三個常駐服務自動恢復，Migration Job 保持已退出。
