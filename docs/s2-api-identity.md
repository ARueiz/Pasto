# S2：房間 API 與匿名身分

最新狀態：使用者已確認測試審查 OK，完整 S2 已由 PR #5 合併至 main `9eace5f`。下文保留各 patch 當時的交付／審查紀錄。S3-01 接續文字寫入與 DELETE room 的 Participant 刪除配額；S5 處理關房通知，S7 處理既有濫用風險。

基準 main `781470a`。依使用者核准，新增 Microsoft.AspNetCore.Mvc.Testing 測試相依；metadata API 歸 S2，S3 沿用並加入文字 API／快照，不另建平行入口。每個 patch 測試後交獨立 review；目前 S2-03 已實作，待獨立 review，不推送或合併。

## 已固定的決策

1. joinUrl 回傳相對路徑 `/r/{code}`，前端 S4 再組完整 URL。不讀 Request.Host、X-Forwarded-Host 或任意輸入拼連結；測試偽造 Host／轉送 Host 不影響輸出。
2. S2-02 由 Application 定義 Participant 驗證器介面；Infrastructure hash 憑證並查 ParticipantId／RoomId。API 的自訂 AuthenticationHandler 解析 Bearer 並呼叫驗證器，S5 Hub 沿用同一驗證來源。驗證器只辨識身分，不判斷房間有效性。順序：缺少／無效憑證 401 → 有效但跨房 401（不查目標房是否存在）→ 同房但失效 404 → 身分有效但缺操作權 403。寫入仍用 S1 鎖內重驗；讀取 use case 亦依 DB 時間檢查。
3. S2-02 Join 先 trim／轉大寫並驗六碼格式，再依 code SELECT FOR UPDATE，鎖後取 clock_timestamp 驗有效性並 COUNT(*) 計 Participant。房主計入、上限 1000、原 /me 恢復不耗名額；所有既有房間新增 Participant 的路徑先鎖 Room。不存在／已過期／已關閉但未清理均回相同 404，不搶用仍被占用的碼。測 999 人時競爭僅一個加入成功，第 1001 人回 409。
4. S2-01 即交付建房每 IP／全站 token bucket、無排隊、可配置配額及可信代理。預設不信任任何 forwarded headers，只用連線 IP；設定 IP allowlist 才採 X-Forwarded-For／Proto，不採 Forwarded-Host。測試用小 bucket 與長補充週期，不等待補充。/me 與 metadata 限流納入 S2-02／03：憑證查 DB 前有 IP／全站粗限制，有效後再套每 Participant 讀取配額；不讓無效憑證無限查 DB。S3 才交付文字新增／刪除配額。S7 延後項目仍依 ADR 0003。
5. S2-01 固定共用錯誤表，後續 patch 沿用。所有回應（包含錯誤、404、405 與 429）都有 Cache-Control: no-store。ProblemDetails 只含安全訊息、code／correlationId，不帶 credential／房碼／SQL／stack trace；503 不加 Retry-After 或自動重送 POST 的提示。

| 結果 | HTTP／code |
| --- | --- |
| RoomUnavailableException | 404 RoomUnavailable |
| RoomParticipantMismatchException、缺少／無效／跨房憑證 | 401 ParticipantUnauthorized |
| RoomOwnershipException | 403 RoomDeleteForbidden |
| ItemOwnershipException（S3） | 403 ItemDeleteForbidden |
| RoomBusyException、RoomCodeAllocationException | 503 ServiceUnavailable |
| DB／未分類持久層故障、commit 期間失敗或取消 | 503 ServiceUnavailable（可能已提交，不宣稱必然回滾） |
| ParticipantLimitReachedException（S2-02 use case） | 409 ParticipantLimitReached |
| RoomItemLimitReachedException（S3） | 409 RoomItemLimitReached |
| InvalidItemContentException／格式錯誤 | 400 InvalidRequest（S3 細分文字錯誤） |
| 限流 | 429 RateLimited＋Retry-After |
| 非 DB 的未預期程式錯誤 | 500 InternalError |

API 不引用 Npgsql／EF 例外型別，透過 Application 的窄介面由 Infrastructure 判定持久層故障。已斷線的請求無法保證送達錯誤回應；不得把 client 取消當成確定回滾，仍不重試交易。

## Patch 順序與驗收

- S2-01：POST /api/rooms → 正式 CreateRoom，201 回 roomId／code／joinUrl／expiresAt／version（十進位字串）／participantId／credential／isCreator。加入共用錯誤處理、no-store、建房限流、可信代理、API pool 預設 Max 64／Min 0 與 ValidateOnStart。正式連線強制關閉 LogParameters／IncludeErrorDetail，EF 不啟用敏感資料記錄，HTTP 不記 header／body。Host 實際 DI 的預設／非法 MaxCodeAttempts、scope／factory 生命週期、啟動驗證、故障與提交後取消用 WebApplicationFactory 驗證。測試套件版本對齊本機 ASP.NET Core 10.0.13，更新 lock file。
- S2-02：Join／Credential 驗證／me；驗證跨房錯誤順序、恢復不建新身分、1000 人競爭及憑證查詢前限流。
- S2-03：GET room metadata 與 DELETE room；同房有效性、Creator 權限、503／取消語意、讀取限流。metadata 不含文字。

S2-01 不先建立尚未使用的驗證器／AuthenticationHandler。S2-02 的公開 Join 沿用 Room→children 鎖序與相同鎖後有效性，不能直接跳過或放寬現有 RoomWriteAccess 的同房檢查。限流候選值不是壓測達標證據；S7 前只在隔離本機環境使用。

## S2-01 設定與交付

| 設定 | 預設／驗證 |
| --- | --- |
| ConnectionStrings:Pasto | 必填；由環境變數 ConnectionStrings__Pasto 或 .NET configuration 注入，不讀 Compose 的 .env |
| Database:MaxPoolSize | 64，允許 1～256；強制 Min Pool Size=0、Pooling=true、LogParameters=false、IncludeErrorDetail=false |
| RoomCreation:MaxCodeAttempts | 沿用 5，允許 1～20，ValidateOnStart |
| Api:CreatePerIp:TokenLimit／TokensPerPeriod／PeriodSeconds | 10／30／60（每分鐘 30，burst 10） |
| Api:CreateGlobal:TokenLimit／TokensPerPeriod／PeriodSeconds | 20／120／60（每分鐘 120，burst 20） |
| Api:TrustedProxies | 空陣列，不信任任何 forwarded headers；設定時逐一驗證 IP address，ForwardLimit=1，只採 X-Forwarded-For／Proto |

Bucket token／補充量允許 1～100000、週期 1～86400 秒；QueueLimit=0，不排隊。IP 正規化 IPv4／mapped IPv6，缺少連線 IP 時共用 unknown 桶，不信任用戶自填 IP。每 IP 與全站以 ASP.NET Core chained token buckets 共同限制；只有正式建房 endpoint 會消耗建房配額。S2-02 會擴充公開加入與憑證查詢前防護，不將任意房碼／token 作 partition key。

API pool 以 host 擁有的 singleton NpgsqlDataSource 共用，host 結束時釋放；DbContextFactory 仍是 singleton，每次交易建立獨立 context，use case／Store 維持 scoped。API 的 IStorageFailureClassifier 窄介面由 Infrastructure 實作，避免 HTTP 錯誤處理引用 EF／Npgsql；root Program 作 DI 組裝可引用 Infrastructure。

建房不需 request body。201 的 Location 與 joinUrl 均為相對路徑；回應 Credential 的 DTO 不使用 record，避免自動 ToString 暴露秘密。未知路由／方法也回 ProblemDetails；錯誤與日誌共用 correlationId。API 4xx 記 Information、503 記 Warning、其他 5xx 記 Error；500 另記例外型別與不含檔案路徑的實際 stack frames，不輸出例外物件／訊息。未啟用 HttpLogging 或 EF sensitive logging。

S2-01 經 Claude 靜態審查，P0～P2 為 0、P3 為 1，未獨立重跑測試；日誌分級已修正，真 PG 房碼衝突測試另證明 EF provider 確實記錄 Error 且不含房碼／回傳憑證。S2-02 的 AuthenticationHandler 必須覆寫 HandleChallengeAsync，自行輸出 ParticipantUnauthorized 與 WWW-Authenticate，避免 StatusCodePages 補成 InvalidRequest。

日誌修正與該測試保留在 S2-01 分支提交 `58e7926`，供審查方獨立實測；S2-02 從此基準建立堆疊分支 `feature/s2-02-join-identity`。不代表 S2-01 已合併或已由審查方重跑。

## S2-01 驗證（2026-11-17）

- 新增 Microsoft.AspNetCore.Mvc.Testing 10.0.13，與本機 ASP.NET Core runtime 對齊；產品專案未新增套件，測試 lock file 已更新且 locked restore 通過。
- RoomApiTests 共 38 例（5 個真 DB、33 個不連 DB 的 HTTP／host 案例）。PastoApiFactory 載入實際 Program；只在需要時注入故障 Store 或 EF interceptor，不複製 DI 註冊。
- 驗證真建房持久化／回應、偽造 Host 與 X-Forwarded-Host 不影響 joinUrl、預設／非法選項啟動驗證、DI scope／factory、pool 與敏感連線選項強制覆寫。
- 驗證可信／不可信代理、IP 桶獨立與 IPv4 mapped 正規化、跨 IP／併發全站 bucket、失敗請求亦計入配額、429 body／Retry-After／no-store；配額測試不等待補充。
- 共用錯誤表使用實際 HTTP pipeline 與注入故障驗證，涵蓋 no-store、correlationId、401 Bearer challenge、503 不帶 Retry-After、回應／日誌不含敏感 sentinel。真建房日誌亦不含回傳的明文 Credential 或房碼。
- 真 DB 案例驗證 commit 前取消完整回滾，以及已 commit 後注入 DB 故障／取消回 503、三筆建房資料仍存在且只提交一次。後者是明確的提交後同步點，不宣稱模擬全部網路 COMMIT／cancel 競爭或 client 真正斷線。
- Release Build 0 warning／0 error，格式與差異檢查通過；完整 158 passed／0 failed／0 skipped，測試後 0 個殘留 schema。命令與 TRX 見 testing.md。
- S2-01 待獨立 review；尚未提供 Join、Credential 驗證器／AuthenticationHandler、me／metadata、關房 HTTP、後續限流與 SignalR。這些依 S2-02／03、S3／S5 交付，不宣稱本 patch 完成整個 S2。

## S2-02 交付與限流配置

- 已提供 POST /api/rooms/join 與 GET /api/rooms/{roomId}/me，沒有新增套件／schema／Migration。JoinRequest 只接受 code，ParticipantId／Creator 由後端決定；joinUrl／Location 仍為相對路徑。
- Join 使用共用 RoomWriteAccess 的依 code 鎖房入口，與原依 id 寫入入口共用鎖後時鐘／有效性。原入口仍檢查同房 Participant，未放寬新增／刪除／關房授權。COUNT(*) 在鎖內，房主計入 1000 人上限；只插 Participant，不更新內容版本或統計。
- Application 的 IParticipantVerifier 由 Infrastructure 實作：先驗固定 22 字元 Base64URL 格式，再對原始字串 SHA-256 查 hash，只回內部 ID，不判斷房間有效性。AuthenticationHandler 只在受保護 endpoint 認證，不讓公開或不存在的 endpoint 因多餘的 Authorization header 查 DB；REST 不接受 query token。
- Handler 自訂 HandleChallengeAsync，輸出 401 ParticipantUnauthorized＋WWW-Authenticate: Bearer；有效但跨房在調用 recovery Store 前回相同 401，無法判斷目標房間存在與否。同房有效憑證遇過期／關房則由 use case 回 404；驗證器仍可辨識身分。Worker 未來刪掉 Participant 後，舊 Credential 自然變成無效 401。
- /me 使用短唯讀 REPEATABLE READ snapshot 與 DB 時間驗有效性，不產生新 Participant，不回傳憑證。關房發生在讀取檢查點後的在途回應仍可能完成，沿用架構的快照語意，不承諾撤回已取得資料。

| Api 設定前綴 | TokenLimit／TokensPerPeriod／PeriodSeconds |
| --- | --- |
| JoinPerIp | 20／120／60 |
| JoinGlobal | 60／30／1 |
| ReadPerIp | 1000／30000／60 |
| ReadGlobal | 2000／60000／60（新增的保守全站候選值，未經壓測） |
| ReadPerParticipant | 40／240／60 |

建房／加入／讀取使用不同配額鍵。UseRateLimiter 在 Authentication 前先套命名 endpoint 的 IP／全站 bucket，避免無效憑證無限查 DB；/me 在成功驗證與同房檢查後，再用驗證後 Participant ID 套讀取 quota。所有拒絕均 429＋Retry-After＋no-store。S2-03 metadata 需接入同一 read admission 與 Participant quota，不能另開未受保護的入口。

S2-02 新增 29 例（20 真 DB、9 HTTP／host 無 DB），完整 188 passed／0 failed／0 skipped，含 S2-01 修正後 39 例與 S1 的 120 例。特別驗證 challenge body 不被 StatusCodePages 改成 InvalidRequest、跨房先拒絕、不重建身分、保留原內容版本、查憑證前限流、999 人競爭、第 1001 人 409、等鎖後關房／到期、提交前後取消／故障。

完整測試後 0 個殘留 schema；S2-02 審查 findings 已處理（見後續紀錄）；S2-03 已實作，沒有推送／合併。分散猜碼、partition 硬上限及負載證據留 S7／S8；S4 靜態資源接入時再將 no-store 限縮到 API 範圍，當前 host 只有 API。

## S2-02 審查修正與風險決策（2026-11-18）

Claude 對 `a614f61..58e7926` 與 `58e7926..a6f465b` 僅做靜態審查，沒有重跑測試；S2-01 日誌修正正確，S2-02 有兩項 P2，無 P0／P1。本輪針對這兩項處理，不開始 S2-03。

### Finding 1：JSON body 上限

Program 將 Kestrel 全域 MaxRequestBodySize 固定為 32768 bytes，在 JSON 反序列化配置大字串之前擋下超限讀取。Minimal API binder 可能自行設定 413，未拋到 ApiFailureMiddleware，因此 StatusCodePages 的 413 也要輸出 RequestBodyTooLarge，不能沿用 InvalidRequest。

新增 RequestBodyLimitTests，使用既有 WebApplicationFactory 的 UseKestrel，監聽 loopback 動態埠，透過真 TCP／HTTP 1.1 測試，沒有新增套件。Content-Length 的 32767／32768 bytes 可進入格式驗證（測試用 code=bad，回 400），32769 bytes 回 413；chunked 測試另涵蓋小 body 與接近／超過上限的拒絕。全部驗證 no-store、ProblemDetails code／correlationId，以及不觸發 Join Store。

**HTTP/1.1 chunked 的限制較保守**：Kestrel 10.0.13 將 chunk framing 也計入上限，接近 32 KiB 的 JSON 即可能回 413；本輪不調高原生安全上限，也不保證所有傳輸編碼都能容納恰好 32768 bytes 的 JSON。依據：[版本對應的 Kestrel 原始碼](https://github.com/dotnet/aspnetcore/blob/v10.0.13/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs)。

### Finding 2：採用方案 (a)，維持配置並接受已知風險

#### S7 第一批失敗計費

S2 維持原設定。S7 第一批加入 `Api:CredentialFailuresPerSource`（burst 20、每秒補 1，無排隊），僅在格式符合且查無身分時扣額。查無的 SHA-256 hash 共用 5 分鐘固定 TTL 快取，上限 10,000 筆，滿載時依寫入順序淘汰最舊項目。命中回 401，跳過 DB 與兩種配額。快取有效期內，同一 hash 的併發查無結果最多扣一次，已在途的查詢仍可完成。

分組使用 IPv4 /32 與 IPv6 /64，IPv4-mapped IPv6 還原為 IPv4。公開建房、Join 和 DB 底線的每來源分組也一併改變。REST、協商與 Hub transport 共用 `ReadPerIp` 1,000／30,000／60 與 `ReadGlobal` 2,000／60,000／60 作為 DB 保護底線。失敗桶先判斷，擋下時回 `429 CredentialFailureRateLimited`、`Retry-After`、`no-store`。缺少／格式錯誤、查無快取命中或失敗桶拒絕的請求都在 DB 底線之外。前端收到協商 401 時停止連線與 REST 重試，進入身分失效流程。

**剩餘風險。** 查詢前檢查與查無後扣額之間有併發窗口，單來源瞬時可使 DB 查詢達 burst 1,000。大量不同 /64 或少數來源的瞬時併發仍可耗盡全站底線，影響跨房讀寫與 Hub 重連。共用 NAT 或 /64 的合法使用者在失敗桶空時也會收到 429。舊憑證快取可降低少數舊憑證的重試成本，但不同舊憑證仍各占一次失敗額度。有效憑證的跨房 REST 請求扣自身讀取配額，仍可能暫時阻擋同房讀取。

查無快取與失敗桶為單一程序記憶體。快取保留最多 10,000 個 64 字元十六進位 hash（約 1.28 MB 字元資料，另有字串與集合開銷）。每來源失敗桶最多 20,000 個，查詢時惰性補充，每分鐘最多掃除一次已補滿且 10 分鐘未扣額的來源。滿載時，既有來源維持各自的桶，新來源退回每來源與全站的 DB 保護底線，查無仍寫入快取。驗證前無法分辨有效與無效憑證，共用失敗桶耗盡會封鎖所有新來源。

**滿載與觀測限制。** 滿載期間的新來源暫停失敗計費，保護退回第一批前的 DB 底線水準。事件 `6102 ParticipantCredentialFailureSourceCapacityReached` 記錄未能計費的查無摘要，每程序最多每 60 秒一筆。事件 `6100 ParticipantCredentialFailuresThrottled` 也只提供拒絕摘要。沒有下一次事件時，最後一段累計可能未寫入日誌。精確的失敗桶拒絕數使用 `http.server.request.duration` 中標為 `pasto.rate_limit.reason=credential_failure` 的 429 次數。6102 暫無專用指標，無法由日誌得出精確未計費數。

大量不同的無效憑證可擠掉 FIFO 快取中的舊憑證，新一輪重送時可能再扣額。既有 ASP.NET Core `PartitionedRateLimiter` 會移除閒置超過 10 秒的桶，create／join／DB 底線及 Participant 的 partition 仍無硬上限。S8 量測分散來源、共用 NAT、partition 數與記憶體峰值。 [.NET 10.0.13 實作](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Threading.RateLimiting/src/System/Threading/RateLimiting/DefaultPartitionedRateLimiter.cs)

另一項既有設計風險亦列入 S7：持有分享連結者可持續 Join 耗盡 1000 個生命週期名額，讓其他人無法加入。Participant 上限是資料預算，不是濫用防護；公開部署前需濫用測試與取捨確認，不能自行刪除被內容引用的 Participant 釋放名額。

修正後 Build 0 warning／0 error，Kestrel 相關測試 7 passed，完整 195 passed／0 failed／0 skipped；格式檢查通過。失敗分析、TRX 與獨立審查界線見 testing.md。

## S2-03 交付與驗證（2026-11-18）

S2-02 findings 已處理，Claude 最終靜態審查無阻擋事項；這不代表審查方已獨立執行測試。本 patch 從 `bc97100` 建立 `feature/s2-03-room-state-closure`，尚未推送或合併。

- GET /api/rooms/{roomId}：Bearer 與同房檢查後，回傳 version（十進位字串）、itemCount、totalBytes、expiresAt、serverTime、limits，不含正文或憑證。短唯讀 REPEATABLE READ 交易取得一致統計及 DB 時間；與 /me 共用 RoomReadAccess，不取得寫入 row lock。
- metadata 與 /me 共用驗證前每 IP／全站 read admission，以及驗證後 Participant ID 的讀取配額。ParticipantReadFilter 先檢查同房，再消耗 Participant 配額。
- DELETE /api/rooms/{roomId}：沿用 CloseRoom／RoomWriteAccess，鎖內檢查有效性、同房與 Creator，提交後回 204。重複關房或遇到已關／到期房間，拋 RoomUnavailableException，由共用錯誤流程回 404 RoomUnavailable。
- DELETE 也使用驗證前 read admission，保護 Credential 查詢；不消耗 Participant 的讀取配額，讀取額度耗盡仍可由房主關房。沒有新增限流設定或改變數值。
- verifier 後房間被實體清理的競爭，由實際讀取／關房 use case 拋例外，不直接回空白 404。關房不刪文字、不釋放房碼或 Participant，清理仍留 S6。

新增 RoomStateApiTests 33 例：字串版本、資料不洩漏、缺少／無效／跨房憑證、失效房間、權限順序、共用配額、驗證前限流、未提交寫入期間的讀取、實體清理競爭、併發關房與提交失敗／取消。既有 Join 提交故障 interceptor 原樣搬至 TransactionTestHooks，供 HTTP 關房共用。

實作方實測：Release Build 0 warning／0 error，相關 33 passed，完整 228 passed／0 failed／0 skipped，format verify 與 diff check 通過；殘留 schema 為 0。TRX 見 testing.md。S2-03 待獨立 review；S3 尚未開始。沒有 schema、Migration 或相依變更，S7 配額與濫用風險維持原狀。
