using System.Text;

namespace Pasto.Server.Application.Items;

public sealed class AddItem(IItemAdditionStore store)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // 呼叫端提供由憑證驗證器取得的身分；S2 接上 Credential，不接受 body 指定 Owner。
    // Store 仍須在房間鎖內重驗 Participant 是否屬於該房。
    public Task<AddedItem> ExecuteAsync(Guid roomId, Guid participantId, string content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidItemContentException(ItemContentError.Empty);
        }

        if (content.Contains('\0'))
        {
            throw new InvalidItemContentException(ItemContentError.InvalidUnicode);
        }

        int bytes;
        try
        {
            // 不用 replacement fallback，避免悄悄改寫不成對 surrogate。
            bytes = StrictUtf8.GetByteCount(content);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidItemContentException(ItemContentError.InvalidUnicode);
        }

        if (bytes > 8192)
        {
            throw new InvalidItemContentException(ItemContentError.TooLarge);
        }

        return store.AddAsync(roomId, participantId, content, bytes, cancellationToken);
    }
}
