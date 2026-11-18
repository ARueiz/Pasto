# Api

S3-01 已接 POST／DELETE items，沿用既有交易 use case；Participant 新增／刪除配額分開，DELETE room 與刪除文字共用刪除配額。驗證前沿用 IP／全站粗 admission。POST body 僅接受 content，正文／傳輸大小限制分別驗證；快照 GET 留 S3-02。

S2-03 已接 GET room metadata 與 DELETE room。metadata 與 /me 共用 ParticipantReadFilter 及兩層讀取限流；DELETE 使用驗證前 admission，並呼叫既有 CloseRoom。失效房間由 use case 拋例外，交共用 ProblemDetails 流程處理。

REST／SignalR 的傳輸與輸入處理層，不直接操作 DbContext。S2-01 已提供 POST /api/rooms，呼叫正式 CreateRoom；回應相對 joinUrl、十進位字串版本與匿名房主 Credential。

共用 middleware 提供 no-store／ProblemDetails／安全日誌；建房使用每 IP＋全站 token bucket 與可信代理設定。持久層故障由 Application 介面分類，API 不引用 EF／Npgsql。設定、錯誤對應與後續身分驗證工作見 docs/s2-api-identity.md。

S2-02 已有 POST /api/rooms/join 與 GET /api/rooms/{roomId}/me。Admission 先於 Authentication，Handler 只在 protected endpoint 查憑證並自訂 challenge body；跨房先拒絕再讀房間，同房成功身分另套 Participant 讀取配額。S5 Hub 尚未接入。
