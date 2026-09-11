using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Adminbot.Services
{
    /// <summary>
    /// Identifies one built-in installation tutorial that a tenant storefront customer can request.
    /// </summary>
    /// <remarks>
    /// These are compile-time constants rather than free-form values because they are the only thing an incoming
    /// Telegram callback may contribute to asset resolution. A callback payload selects a kind from this closed set; it
    /// can never select a filesystem path, another tenant bot, or an arbitrary directory.
    /// </remarks>
    public static class TenantTutorialKinds
    {
        /// <summary>Android installation guide using the v2rayNG client.</summary>
        public const string Android = "android";

        /// <summary>iOS installation guide using the V2Box client, which is also usable on Android.</summary>
        public const string Ios = "ios";

        /// <summary>Windows installation guide using the v2rayN client.</summary>
        public const string Windows = "windows";

        /// <summary>Every supported tutorial kind, in the order the customer menu presents them.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Android, Ios, Windows };

        /// <summary>
        /// Determines whether a value is one of the three supported tutorial kinds.
        /// </summary>
        /// <param name="kind">Candidate value taken from a Telegram callback payload.</param>
        /// <returns><c>true</c> only for an exact match against a supported kind constant.</returns>
        /// <remarks>
        /// The comparison is ordinal and case sensitive, so an unexpected or approximate payload is rejected rather than
        /// silently mapped onto a real tutorial directory.
        /// </remarks>
        public static bool IsSupported(string kind)
            => kind == Android || kind == Ios || kind == Windows;
    }

    /// <summary>
    /// Outcome of resolving the local image set for one built-in tutorial.
    /// </summary>
    public enum TenantTutorialAssetStatus
    {
        /// <summary>At least one supported image was found and the set can be delivered.</summary>
        Available = 0,

        /// <summary>The tutorial directory does not exist in the deployed layout.</summary>
        Missing = 1,

        /// <summary>The tutorial directory exists but contains no supported image.</summary>
        Empty = 2,

        /// <summary>The requested value is not a supported tutorial kind.</summary>
        UnsupportedKind = 3
    }

    /// <summary>
    /// Ordered, naturally sorted local image set for one built-in tutorial, or the reason it is unavailable.
    /// </summary>
    /// <remarks>
    /// The result carries absolute paths for the local file system only. Those paths are for internal logging and for
    /// opening streams; they must never be sent to a Telegram customer.
    /// </remarks>
    public sealed class TenantTutorialAssetResult
    {
        /// <summary>Gets the requested tutorial kind.</summary>
        public string Kind { get; init; }

        /// <summary>Gets the repository/publish-relative directory the images were expected in.</summary>
        /// <remarks>
        /// This is the safe value to log and to show to an operator. It never contains a machine-specific root.
        /// </remarks>
        public string RelativeDirectory { get; init; }

        /// <summary>Gets the resolution outcome.</summary>
        public TenantTutorialAssetStatus Status { get; init; }

        /// <summary>Gets the naturally ordered absolute image paths; empty unless <see cref="IsAvailable" /> is true.</summary>
        public IReadOnlyList<string> ImagePaths { get; init; } = Array.Empty<string>();

        /// <summary>Gets the album caption for this tutorial, already localised for the customer.</summary>
        public string Caption { get; init; } = string.Empty;

        /// <summary>Gets a value indicating whether the image set can be delivered.</summary>
        public bool IsAvailable => Status == TenantTutorialAssetStatus.Available && ImagePaths.Count > 0;
    }

    /// <summary>
    /// Resolves built-in tenant installation-tutorial image sets from the application's deployed asset directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service exists so that tutorial assets are described in exactly one place. It maps a closed set of tutorial
    /// kinds to fixed directories, enumerates only Telegram-compatible image files, sorts them in natural step order, and
    /// reports availability. It performs no Telegram I/O and no database work, and it never reads tenant configuration:
    /// customer tutorials are built in, not owner-configured.
    /// </para>
    /// <para>
    /// Asset resolution is deliberately independent of the process current working directory. The root is derived from
    /// <see cref="AppContext.BaseDirectory" />, which is the directory the running assembly was loaded from and therefore
    /// the published layout root under both the console host and systemd.
    /// </para>
    /// <para>
    /// Directory names are repository/publish-relative. No development-machine path is ever referenced.
    /// </para>
    /// </remarks>
    public static class TenantTutorialAssetService
    {
        /// <summary>Telegram's hard limit on the number of items in a single media group.</summary>
        /// <remarks>
        /// A media group with more than ten items is rejected by Telegram, so callers must split larger sets into
        /// multiple albums. Splitting is deterministic and preserves image order.
        /// </remarks>
        public const int MaxMediaGroupItems = 10;

        /// <summary>Repository/publish-relative root that holds every built-in tutorial directory.</summary>
        public const string TutorialRootRelativePath = "Assets/tutorials";

        /// <summary>
        /// Image extensions that are actually used by the shipped tutorial assets and are accepted as album photos.
        /// </summary>
        /// <remarks>
        /// Only formats present in the repository are listed. <c>.webp</c> is deliberately excluded because it is not
        /// shipped here and its photo-upload behaviour was not verified against the pinned Telegram client.
        /// </remarks>
        private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png" };

        /// <summary>
        /// Resolves the absolute tutorial asset root for the running application.
        /// </summary>
        /// <returns>The absolute path of <c>Assets/tutorials</c> beside the loaded assembly.</returns>
        /// <remarks>
        /// Uses <see cref="AppContext.BaseDirectory" /> rather than the current working directory so a systemd service
        /// that starts with an unrelated working directory still finds the published assets.
        /// </remarks>
        public static string ResolveTutorialRoot()
            => Path.Combine(AppContext.BaseDirectory, "Assets", "tutorials");

        /// <summary>
        /// Gets the directory name inside the tutorial root that holds one tutorial's images.
        /// </summary>
        /// <param name="kind">One of the <see cref="TenantTutorialKinds" /> values.</param>
        /// <returns>The directory name, for example <c>android_v2rayng</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind" /> is not a supported tutorial kind.</exception>
        /// <remarks>
        /// The mapping is exhaustive and explicit so an unrecognized kind fails loudly instead of silently resolving to
        /// some other tutorial's images.
        /// </remarks>
        public static string ResolveDirectoryName(string kind) => kind switch
        {
            TenantTutorialKinds.Android => "android_v2rayng",
            TenantTutorialKinds.Ios => "ios_android_v2box",
            TenantTutorialKinds.Windows => "windows_v2rayn",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported tenant tutorial kind.")
        };

        /// <summary>
        /// Gets the customer-facing album caption for one tutorial.
        /// </summary>
        /// <param name="kind">One of the <see cref="TenantTutorialKinds" /> values.</param>
        /// <returns>Persian caption text placed on the first photo of the first album.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind" /> is not a supported tutorial kind.</exception>
        /// <remarks>
        /// The iOS caption names V2Box and states that the app also works on Android, because V2Box runs on both
        /// platforms. Android keeps its own dedicated v2rayNG tutorial and is never redirected to the V2Box set.
        /// </remarks>
        public static string ResolveCaption(string kind) => kind switch
        {
            TenantTutorialKinds.Android => "🤖 آموزش نصب Android با v2rayNG",
            TenantTutorialKinds.Ios => "🍎 آموزش نصب با V2Box\nاین نرم‌افزار برای iOS و Android قابل استفاده است.",
            TenantTutorialKinds.Windows => "🪟 آموزش نصب ویندوز با v2rayN",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported tenant tutorial kind.")
        };

        /// <summary>
        /// Resolves the ordered image set for one built-in tutorial.
        /// </summary>
        /// <param name="kind">
        /// Tutorial kind taken from the closed <see cref="TenantTutorialKinds" /> set. It originates from our own
        /// keyboard, never from free-form customer input, and an unsupported value is rejected rather than mapped.
        /// </param>
        /// <param name="tutorialRootOverride">
        /// Optional absolute tutorial root used by tests. Production callers pass <c>null</c> so the deployed
        /// <see cref="ResolveTutorialRoot" /> location is used.
        /// </param>
        /// <returns>
        /// A result whose <see cref="TenantTutorialAssetResult.ImagePaths" /> are naturally ordered absolute paths when
        /// available, or an unavailable status describing why the set cannot be delivered. The result is never
        /// <c>null</c> and never throws for a missing, empty, or unreadable directory.
        /// </returns>
        /// <remarks>
        /// Enumeration is flat and restricted to the one fixed tutorial directory. Unrelated files such as
        /// <c>.DS_Store</c>, <c>Thumbs.db</c>, and text metadata are ignored, and subdirectories are never traversed, so
        /// no file outside the intended tutorial folder can be uploaded.
        /// </remarks>
        /// <example>
        /// <code>
        /// var assets = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Android);
        /// if (!assets.IsAvailable)
        ///     logger.LogWarning("Tutorial assets unavailable. kind={Kind}, dir={Dir}", assets.Kind, assets.RelativeDirectory);
        /// </code>
        /// </example>
        public static TenantTutorialAssetResult Resolve(string kind, string tutorialRootOverride = null)
        {
            if (!TenantTutorialKinds.IsSupported(kind))
                return new TenantTutorialAssetResult
                {
                    Kind = kind,
                    RelativeDirectory = TutorialRootRelativePath,
                    Status = TenantTutorialAssetStatus.UnsupportedKind
                };

            var directoryName = ResolveDirectoryName(kind);
            var relativeDirectory = $"{TutorialRootRelativePath}/{directoryName}";
            var root = string.IsNullOrWhiteSpace(tutorialRootOverride)
                ? ResolveTutorialRoot()
                : tutorialRootOverride;
            var absoluteDirectory = Path.Combine(root, directoryName);

            if (!Directory.Exists(absoluteDirectory))
                return new TenantTutorialAssetResult
                {
                    Kind = kind,
                    RelativeDirectory = relativeDirectory,
                    Status = TenantTutorialAssetStatus.Missing
                };

            List<string> files;
            try
            {
                // Flat enumeration only: files directly inside this tutorial's own directory.
                files = Directory.EnumerateFiles(absoluteDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedImage)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // An unreadable directory is reported as unavailable rather than surfacing a raw I/O error to the update
                // pipeline, so a filesystem problem can never affect customer state, orders, or payments.
                return new TenantTutorialAssetResult
                {
                    Kind = kind,
                    RelativeDirectory = relativeDirectory,
                    Status = TenantTutorialAssetStatus.Missing
                };
            }

            if (files.Count == 0)
                return new TenantTutorialAssetResult
                {
                    Kind = kind,
                    RelativeDirectory = relativeDirectory,
                    Status = TenantTutorialAssetStatus.Empty
                };

            // Natural ordering keeps installation steps in 1,2,...,10 order instead of lexicographic 1,10,2 order.
            // The ordinal tiebreak makes the result deterministic even if two names compare equal naturally.
            var ordered = files
                .OrderBy(path => Path.GetFileName(path), NaturalComparer.Instance)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();

            return new TenantTutorialAssetResult
            {
                Kind = kind,
                RelativeDirectory = relativeDirectory,
                Status = TenantTutorialAssetStatus.Available,
                ImagePaths = ordered,
                Caption = ResolveCaption(kind)
            };
        }

        /// <summary>
        /// Splits an ordered image list into the batches that will each become one Telegram album.
        /// </summary>
        /// <param name="imagePaths">Naturally ordered image paths for a single tutorial.</param>
        /// <returns>
        /// Consecutive batches of at most <see cref="MaxMediaGroupItems" /> items, preserving the input order. The result
        /// is empty when the input is empty.
        /// </returns>
        /// <remarks>
        /// Splitting is deterministic and lossless: item 11 lands in the second batch rather than being dropped. Callers
        /// still handle a single-item list through the single-photo path, because a one-item media group is invalid.
        /// </remarks>
        /// <example>
        /// <code>
        /// foreach (var batch in TenantTutorialAssetService.BatchForMediaGroups(assets.ImagePaths))
        ///     await SendBatchAsync(batch, cancellationToken);
        /// </code>
        /// </example>
        public static IReadOnlyList<IReadOnlyList<string>> BatchForMediaGroups(IReadOnlyList<string> imagePaths)
        {
            var result = new List<IReadOnlyList<string>>();
            if (imagePaths == null || imagePaths.Count == 0)
                return result;

            for (var offset = 0; offset < imagePaths.Count; offset += MaxMediaGroupItems)
            {
                var size = Math.Min(MaxMediaGroupItems, imagePaths.Count - offset);
                var batch = new List<string>(size);
                for (var index = 0; index < size; index++)
                    batch.Add(imagePaths[offset + index]);
                result.Add(batch);
            }

            return result;
        }

        /// <summary>
        /// Determines whether a file name is a Telegram-compatible tutorial image we are willing to upload.
        /// </summary>
        /// <param name="path">Absolute or relative file path to test.</param>
        /// <returns><c>true</c> when the extension is one of the supported image formats.</returns>
        /// <remarks>
        /// Extension matching is case-insensitive so <c>1.PNG</c> is accepted. Directory markers and unrelated metadata
        /// files have non-matching extensions and are therefore ignored rather than uploaded.
        /// </remarks>
        public static bool IsSupportedImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension))
                return false;

            foreach (var supported in SupportedExtensions)
            {
                if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Compares two file names using natural ordering so digit runs sort by numeric value.
        /// </summary>
        /// <param name="left">First file name.</param>
        /// <param name="right">Second file name.</param>
        /// <returns>A negative value, zero, or a positive value following the usual comparison contract.</returns>
        /// <remarks>
        /// Compares digit runs numerically and everything else ordinally, case-insensitively for the alphabetic part.
        /// Leading zeros are ignored for the numeric comparison, and a longer digit run orders after a shorter one so
        /// <c>10</c> follows <c>9</c> instead of preceding <c>2</c>.
        /// </remarks>
        public static int CompareNatural(string left, string right)
        {
            left ??= string.Empty;
            right ??= string.Empty;

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftIsDigit = char.IsAsciiDigit(left[leftIndex]);
                var rightIsDigit = char.IsAsciiDigit(right[rightIndex]);

                if (leftIsDigit && rightIsDigit)
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftStart < left.Length && left[leftStart] == '0')
                        leftStart++;
                    while (rightStart < right.Length && right[rightStart] == '0')
                        rightStart++;

                    var leftEnd = leftStart;
                    while (leftEnd < left.Length && char.IsAsciiDigit(left[leftEnd]))
                        leftEnd++;
                    var rightEnd = rightStart;
                    while (rightEnd < right.Length && char.IsAsciiDigit(right[rightEnd]))
                        rightEnd++;

                    var leftLength = leftEnd - leftStart;
                    var rightLength = rightEnd - rightStart;
                    if (leftLength != rightLength)
                        return leftLength < rightLength ? -1 : 1;

                    for (var offset = 0; offset < leftLength; offset++)
                    {
                        var leftChar = left[leftStart + offset];
                        var rightChar = right[rightStart + offset];
                        if (leftChar != rightChar)
                            return leftChar < rightChar ? -1 : 1;
                    }

                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                var leftNormalized = char.ToUpperInvariant(left[leftIndex]);
                var rightNormalized = char.ToUpperInvariant(right[rightIndex]);
                if (leftNormalized != rightNormalized)
                    return leftNormalized < rightNormalized ? -1 : 1;

                leftIndex++;
                rightIndex++;
            }

            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }

        /// <summary>
        /// Singleton comparer that applies <see cref="CompareNatural" /> to file names.
        /// </summary>
        /// <remarks>Allocated once so ordering many files does not allocate a comparer per element.</remarks>
        private sealed class NaturalComparer : IComparer<string>
        {
            /// <summary>Shared comparer instance.</summary>
            public static readonly NaturalComparer Instance = new();

            /// <inheritdoc />
            public int Compare(string x, string y) => CompareNatural(x, y);
        }
    }
}
