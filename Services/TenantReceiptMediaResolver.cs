using System;
using System.Linq;
using Telegram.Bot.Types;

namespace Adminbot.Services
{
    /// <summary>
    /// Identifies which Telegram message field supplied a tenant card-to-card receipt image.
    /// </summary>
    /// <remarks>
    /// The kind is audit/operational metadata only. Both kinds resolve to a plain Telegram file id that is downloaded
    /// through the tenant bot that received it; nothing downstream branches financial or order behavior on this value.
    /// </remarks>
    public static class TenantReceiptMediaKinds
    {
        /// <summary>The receipt arrived as a normal Telegram photo message.</summary>
        public const string Photo = "photo";

        /// <summary>The receipt arrived as a Telegram document whose bytes are a supported image.</summary>
        public const string Document = "document";
    }

    /// <summary>
    /// Resolved receipt image taken from one incoming tenant customer message.
    /// </summary>
    /// <param name="FileId">
    /// Telegram file identifier to persist and later download through the tenant bot that received the message. It is
    /// opaque to this application and never reused in a different bot.
    /// </param>
    /// <param name="Kind">One of the <see cref="TenantReceiptMediaKinds" /> values, for diagnostics only.</param>
    /// <param name="FileName">
    /// Customer-supplied file name for a document receipt, or <c>null</c> for a photo. It is untrusted display metadata
    /// and must never be treated as a filesystem path.
    /// </param>
    public sealed record TenantReceiptMedia(string FileId, string Kind, string FileName);

    /// <summary>
    /// Resolves the image inside one incoming Telegram message without trusting customer input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Production incident: a tenant customer completed the card-to-card flow, pressed the
    /// receipt button, and sent the receipt as a Telegram <i>document</i> ("send without compression"). The legacy
    /// handler only looked at <see cref="Message.Photo" />, so no receipt row, no owner-notification outbox row, and no
    /// provisional operation were ever created and the order sat silently in <c>awaiting_receipt</c>.
    /// </para>
    /// <para>
    /// <b>What is accepted.</b> A real Telegram photo (the largest rendition wins, because that is the highest quality
    /// the sender allowed), or a document that is provably an image through a supported MIME type
    /// (<c>image/jpeg</c>, <c>image/png</c>, <c>image/webp</c>) or a supported file extension
    /// (<c>.jpg</c>, <c>.jpeg</c>, <c>.png</c>, <c>.webp</c>). The extension fallback exists because Telegram omits the
    /// MIME type for some forwarded and re-uploaded images.
    /// </para>
    /// <para>
    /// <b>What is refused.</b> PDF, ZIP, executables, video, audio, and a bare <c>application/octet-stream</c> that
    /// carries no supported extension. A customer file name is never used as a path and never widens what is accepted.
    /// </para>
    /// </remarks>
    public static class TenantReceiptMediaResolver
    {
        /// <summary>Supported image MIME types, compared case-insensitively.</summary>
        private static readonly string[] SupportedContentTypes =
        {
            "image/jpeg",
            "image/jpg",
            "image/pjpeg",
            "image/png",
            "image/webp"
        };

        /// <summary>Supported image extensions, compared case-insensitively.</summary>
        private static readonly string[] SupportedExtensions =
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp"
        };

        /// <summary>
        /// Attempts to resolve one acceptable receipt image from an incoming Telegram message.
        /// </summary>
        /// <param name="message">
        /// Incoming customer message. A photo is preferred when present; otherwise a supported image document is used.
        /// May be <c>null</c>, in which case the method returns <c>false</c>.
        /// </param>
        /// <param name="media">
        /// Receives the resolved file id, its kind, and the untrusted document file name when the method returns
        /// <c>true</c>; otherwise <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the message carries a Telegram photo or a supported image document; <c>false</c> when it
        /// carries nothing usable, or an unsupported document type that must not be stored as a payment receipt.
        /// </returns>
        /// <remarks>
        /// Pure and side-effect free, so both the message router and the receipt handler can call it. The caller owns all
        /// persistence, order binding, and customer replies.
        /// </remarks>
        /// <example>
        /// <code>
        /// if (TenantReceiptMediaResolver.TryResolve(message, out var media))
        ///     await PersistReceiptAsync(media.FileId, cancellationToken);
        /// </code>
        /// </example>
        public static bool TryResolve(Message message, out TenantReceiptMedia media)
        {
            media = null;
            if (message == null)
                return false;

            var largestPhoto = message.Photo?
                .Where(x => !string.IsNullOrWhiteSpace(x.FileId))
                .OrderByDescending(x => x.FileSize ?? 0)
                .FirstOrDefault();

            if (largestPhoto != null)
            {
                media = new TenantReceiptMedia(largestPhoto.FileId, TenantReceiptMediaKinds.Photo, null);
                return true;
            }

            var document = message.Document;
            if (document == null || string.IsNullOrWhiteSpace(document.FileId))
                return false;

            // Telegram marks forwarded/re-uploaded images inconsistently, so a supported extension is accepted as an
            // equally strong signal to a supported MIME type. Everything else fails closed.
            var hasSupportedContentType = !string.IsNullOrWhiteSpace(document.MimeType)
                && SupportedContentTypes.Contains(document.MimeType.Trim(), StringComparer.OrdinalIgnoreCase);
            var hasSupportedExtension = HasSupportedExtension(document.FileName);

            if (!hasSupportedContentType && !hasSupportedExtension)
                return false;

            // An unsupported MIME type is never rescued by an extension: a ".jpg"-named PDF must not become a receipt.
            if (!string.IsNullOrWhiteSpace(document.MimeType) && !hasSupportedContentType)
                return false;

            media = new TenantReceiptMedia(document.FileId, TenantReceiptMediaKinds.Document, document.FileName);
            return true;
        }

        /// <summary>
        /// Determines whether an untrusted customer file name ends in one of the supported image extensions.
        /// </summary>
        /// <param name="fileName">
        /// Customer-supplied document name. May be <c>null</c> or empty, in which case the result is <c>false</c>.
        /// </param>
        /// <returns><c>true</c> when the name ends in a supported image extension; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// The value is treated as opaque text. It is never combined into a path, so directory traversal in a crafted
        /// name has no effect.
        /// </remarks>
        private static bool HasSupportedExtension(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var trimmed = fileName.Trim();
            foreach (var extension in SupportedExtensions)
            {
                if (trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
