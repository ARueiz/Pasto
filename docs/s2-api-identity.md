# S2：房間 API 與匿名身分

基準 main `781470a`。依使用者核准，新增 Microsoft.AspNetCore.Mvc.Testing 測試相依；metadata API 歸 S2，S3 沿用並加入文字 API／快照，不另建平行入口。每個 patch 測試後交獨立 review；本次只做 S2-01，不推送或合併。

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

建房不需 request body。201 的 Location 與 joinUrl 均為相對路徑；回應 Credential 的 DTO 不使用 record，避免自動 ToString 暴露秘密。未知路由／方法也回 ProblemDetails；錯誤與日誌共用 correlationId。日誌只記安全錯誤碼及 correlationId，不輸出例外物件／訊息；未啟用 HttpLogging 或 EF sensitive logging。

## S2-01 驗證（2026-11-17）

- 新增 Microsoft.AspNetCore.Mvc.Testing 10.0.13，與本機 ASP.NET Core runtime 對齊；產品專案未新增套件，測試 lock file 已更新且 locked restore 通過。
- RoomApiTests 共 38 例（5 個真 DB、33 個不連 DB 的 HTTP／host 案例）。PastoApiFactory 載入實際 Program；只在需要時注入故障 Store 或 EF interceptor，不複製 DI 註冊。
- 驗證真建房持久化／回應、偽造 Host 與 X-Forwarded-Host 不影響 joinUrl、預設／非法選項啟動驗證、DI scope／factory、pool 與敏感連線選項強制覆寫。
- 驗證可信／不可信代理、IP 桶獨立與 IPv4 mapped 正規化、跨 IP／併發全站 bucket、失敗請求亦計入配額、429 body／Retry-After／no-store；配額測試不等待補充。
- 共用錯誤表使用實際 HTTP pipeline 與注入故障驗證，涵蓋 no-store、correlationId、401 Bearer challenge、503 不帶 Retry-After、回應／日誌不含敏感 sentinel。真建房日誌亦不含回傳的明文 Credential 或房碼。
- 真 DB 案例驗證 commit 前取消完整回滾，以及已 commit 後注入 DB 故障／取消回 503、三筆建房資料仍存在且只提交一次。後者是明確的提交後同步點，不宣稱模擬全部網路 COMMIT／cancel 競爭或 client 真正斷線。
- Release Build 0 warning／0 error，格式與差異檢查通過；完整 158 passed／0 failed／0 skipped，測試後 0 個殘留 schema。命令與 TRX 見 testing.md。
- S2-01 待獨立 review；尚未提供 Join、Credential 驗證器／AuthenticationHandler、me／metadata、關房 HTTP、後續限流與 SignalR。這些依 S2-02／03、S3／S5 交付，不宣稱本 patch 完成整個 S2。
