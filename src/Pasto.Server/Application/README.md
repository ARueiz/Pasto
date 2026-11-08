# Application

Use case 與交易協調層。`Rooms/CreateRoom.cs` 提供正式建房流程，負責有限房碼重試與成功結果；透過 `IRoomCreationStore` 要求一次原子寫入，透過 `IRoomSecrets` 產生房碼／憑證。Application 不引用 EF／Npgsql，也不建立通用 Repository。

Store 回傳 `null` 只表示房碼保留衝突；其餘例外直接往上回報，不自動重送可能已提交的操作。HTTP 錯誤對應與分享 URL 在 S2 處理。
