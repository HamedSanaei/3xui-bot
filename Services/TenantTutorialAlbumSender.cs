using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Adminbot.Services
{
    /// <summary>
    /// Uploads a resolved tutorial image set to a Telegram customer as real photo albums (media groups).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Albums, not separate messages: Telegram renders a media group as one swipeable slideshow, which is what an
    /// installation guide needs. This helper exists so that batching, caption placement, and file-stream lifetime are
    /// handled in exactly one place instead of inside <c>TenantBotService</c>.
    /// </para>
    /// <para>
    /// The helper performs no database work and touches no customer, order, wallet, XUI, or tenant configuration state.
    /// A failed upload therefore cannot affect anything except the customer-visible message.
    /// </para>
    /// <para>
    /// It is a static helper rather than an injected service because it is stateless: it owns no caches, no background
    /// work, and no lifetimes beyond the single call.
    /// </para>
    /// </remarks>
    public static class TenantTutorialAlbumSender
    {
        /// <summary>
        /// Sends every image of one tutorial as one or more Telegram photo albums.
        /// </summary>
        /// <param name="botClient">Tenant bot client that serves the requesting customer.</param>
        /// <param name="chatId">Customer chat that requested the tutorial.</param>
        /// <param name="assets">
        /// Resolved image set for one tutorial. Must be available; callers handle the unavailable case separately so the
        /// customer receives the friendly message instead of an exception.
        /// </param>
        /// <param name="logger">
        /// Local operational logger. Used only for local diagnostics and never routed through the Telegram logger channel,
        /// so a Telegram outage cannot turn an upload failure into recursive Telegram traffic.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the upload and stream lifetime.</param>
        /// <returns>
        /// <c>true</c> when every batch was accepted by Telegram; <c>false</c> when a batch failed. A partially delivered
        /// guide returns <c>false</c> after attempting the remaining batches so the customer still receives as much of the
        /// guide as possible.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Batching follows <see cref="TenantTutorialAssetService.BatchForMediaGroups" />: at most ten items per album,
        /// preserving step order. A single image uses <c>SendPhotoAsync</c> because a one-item media group is invalid.
        /// </para>
        /// <para>
        /// Only the current batch's file streams are open at any moment, and they are disposed as soon as that batch's
        /// request completes, on success and on failure alike. Streams are never cached across calls.
        /// </para>
        /// <para>
        /// The caption is placed on the first photo of the first album only. Telegram would otherwise repeat the same text
        /// on every slide.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var assets = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Android);
        /// if (!assets.IsAvailable)
        ///     await botClient.SendTextMessageAsync(chatId, unavailableText, cancellationToken: cancellationToken);
        /// else
        ///     await TenantTutorialAlbumSender.SendAsync(botClient, chatId, assets, logger, cancellationToken);
        /// </code>
        /// </example>
        public static async Task<bool> SendAsync(
            ITelegramBotClient botClient,
            ChatId chatId,
            TenantTutorialAssetResult assets,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (botClient == null)
                throw new ArgumentNullException(nameof(botClient));
            if (assets == null || !assets.IsAvailable)
                return false;

            var batches = TenantTutorialAssetService.BatchForMediaGroups(assets.ImagePaths);
            var allDelivered = true;

            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];
                // Only the first photo of the first album carries the tutorial caption.
                var caption = batchIndex == 0 ? assets.Caption : null;

                cancellationToken.ThrowIfCancellationRequested();
                if (!await SendBatchAsync(botClient, chatId, batch, caption, assets, logger, cancellationToken))
                    allDelivered = false;
            }

            return allDelivered;
        }

        /// <summary>
        /// Sends one already-batched slice of a tutorial as either a single photo or one media group.
        /// </summary>
        /// <param name="botClient">Tenant bot client that serves the requesting customer.</param>
        /// <param name="chatId">Customer chat that requested the tutorial.</param>
        /// <param name="batch">One to ten ordered absolute image paths.</param>
        /// <param name="caption">Caption for this batch, or <c>null</c> when the batch carries no caption.</param>
        /// <param name="assets">Owning asset result, used only for safe diagnostic fields.</param>
        /// <param name="logger">Local operational logger used for safe, path-free failure diagnostics.</param>
        /// <param name="cancellationToken">Cancellation token for the upload and stream lifetime.</param>
        /// <returns><c>true</c> when Telegram accepted this batch; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Every opened stream is closed in a <c>finally</c> block, so cancellation during an upload cannot leak file
        /// handles. The failure log records only the tutorial kind, the relative directory, and the batch size; it never
        /// records an absolute server path or a raw Telegram response.
        /// </remarks>
        private static async Task<bool> SendBatchAsync(
            ITelegramBotClient botClient,
            ChatId chatId,
            IReadOnlyList<string> batch,
            string caption,
            TenantTutorialAssetResult assets,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
                return true;

            var streams = new List<FileStream>(batch.Count);
            try
            {
                foreach (var path in batch)
                    streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

                if (batch.Count == 1)
                {
                    // A one-item media group is rejected by Telegram, so a single image is sent as a normal photo.
                    await botClient.SendPhotoAsync(
                        chatId: chatId,
                        photo: InputFile.FromStream(streams[0], Path.GetFileName(batch[0])),
                        caption: caption,
                        cancellationToken: cancellationToken);
                    return true;
                }

                var media = new List<IAlbumInputMedia>(batch.Count);
                for (var index = 0; index < batch.Count; index++)
                {
                    // The media payload is constructor-only in the pinned Telegram client, so the stream is supplied at
                    // construction and the caption is set afterwards.
                    media.Add(new InputMediaPhoto(InputFile.FromStream(streams[index], Path.GetFileName(batch[index])))
                    {
                        // The caption belongs to the first slide only, so later slides stay clean.
                        Caption = index == 0 ? caption : null
                    });
                }

                await botClient.SendMediaGroupAsync(
                    chatId: chatId,
                    media: media,
                    cancellationToken: cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Local operational log only. Absolute paths and raw provider payloads are deliberately not logged, and
                // this never reaches the Telegram logger channel, so an outage cannot recursively amplify itself.
                logger?.LogWarning(ex,
                    "Tenant tutorial album delivery failed. kind={TutorialKind}, dir={RelativeDirectory}, images={ImageCount}",
                    assets.Kind,
                    assets.RelativeDirectory,
                    batch.Count);
                return false;
            }
            finally
            {
                // Streams must stay alive until the request above completes, then be released immediately.
                foreach (var stream in streams)
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        // Disposal is best effort; a failing handle must not mask the delivery result.
                    }
                }
            }
        }
    }
}
