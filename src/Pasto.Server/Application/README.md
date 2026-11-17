# Application

Use case 與交易協調層。`Rooms/CreateRoom.cs` 提供正式建房流程，負責有限房碼重試與成功結果；透過 `IRoomCreationStore` 要求一次原子寫入，透過 `IRoomSecrets` 產生房碼／憑證。Application 不引用 EF／Npgsql，也不建立通用 Repository。

Store 回傳 `null` 只表示房碼保留衝突；其餘例外直接往上回報，不自動重送可能已提交的操作。S2-01 已接建房 HTTP 錯誤對應與相對分享 URL。

S1 另提供 Items/AddItem、DeleteOwnItem 與 Rooms/CloseRoom，交易及鎖由 Infrastructure 實作。IStorageFailureClassifier 只提供儲存故障分類給 API；Participant 驗證器與 Join／恢復留 S2-02，metadata／關房 HTTP 留 S2-03。
