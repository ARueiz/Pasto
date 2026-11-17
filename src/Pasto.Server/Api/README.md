# Api

REST／SignalR 的傳輸與輸入處理層，不直接操作 DbContext。S2-01 已提供 POST /api/rooms，呼叫正式 CreateRoom；回應相對 joinUrl、十進位字串版本與匿名房主 Credential。

共用 middleware 提供 no-store／ProblemDetails／安全日誌；建房使用每 IP＋全站 token bucket 與可信代理設定。持久層故障由 Application 介面分類，API 不引用 EF／Npgsql。設定、錯誤對應與後續身分驗證工作見 docs/s2-api-identity.md。
