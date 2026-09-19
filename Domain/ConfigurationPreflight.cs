using System;
using System.Collections.Generic;
using Adminbot.Domain.Logging;

namespace Adminbot.Domain
{
    /// <summary>
    /// Validates cross-cutting application configuration that no single feature validators owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two entry points with deliberately different failure policies:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="ValidateEnabledFeatures"/> is fatal. <c>Program.Main</c> calls it beside the existing per-feature
    /// validators, before dependency injection, database migration, and hosted services. It rejects only a
    /// configuration that cannot work at all: an explicitly enabled feature whose required panel URL is missing or
    /// unusable, a panel URL that is not an absolute HTTP/HTTPS address, or two application databases pointed at the
    /// same file.
    /// </description></item>
    /// <item><description>
    /// <see cref="DescribeStartupReport"/> is warn-only and runs after bot hydration. Telegram logging destinations
    /// belong to this group on purpose: the process must keep settling payments, running XUI work, and answering
    /// customers even when logging is misconfigured, so a missing log destination is a sanitized warning and never a
    /// startup failure.
    /// </description></item>
    /// </list>
    /// <para>
    /// This type never reads, rewrites, prunes, or backfills a configuration file. It observes the already-bound
    /// <see cref="AppConfig"/> and returns strings, so a configuration file can only ever be changed by a human. Keys
    /// that this build does not know about are ignored by the configuration binder and are never reported as errors,
    /// so a newer configuration stays forward-compatible with an older build and an older configuration stays
    /// forward-compatible with a newer build.
    /// </para>
    /// <para>
    /// No message produced here contains a bot token, API key, panel secret, or configured destination value. Panel
    /// URLs, chat ids, and database paths are described by their property name and, where a reason is needed, by a
    /// closed-vocabulary code.
    /// </para>
    /// </remarks>
    public static class ConfigurationPreflight
    {
        /// <summary>
        /// Rejects the whole configuration when an enabled feature cannot work or when a value is structurally wrong.
        /// </summary>
        /// <param name="appConfig">
        /// Application configuration bound from the runtime configuration file. A missing optional key keeps the
        /// documented default declared on <see cref="AppConfig"/>; only values that make the configured features
        /// unusable are rejected.
        /// </param>
        /// <remarks>
        /// Rules, and why each one is fatal:
        /// <list type="number">
        /// <item><description>
        /// A non-blank <c>xuiV3ApiBaseUrl</c> that is not an absolute HTTP/HTTPS URL. Every XUI v3 client builds its
        /// request URI from this value, so a structurally wrong value can never reach a panel. Blank is handled by the
        /// next rule instead, because an unconfigured optional key must stay tolerated.
        /// </description></item>
        /// <item><description>
        /// <c>VolumeExpirationReminderEnabled</c> is true while <c>xuiV3ApiBaseUrl</c> is missing or unusable. The
        /// reminder worker builds its panel descriptor from that URL and throws on every cycle, so the feature would
        /// fail repeatedly at runtime with no chance of recovery. This mirrors the existing fatal checks for the
        /// Tetraminator and AtlasPay gateways, which also refuse to start when enabled without their required settings.
        /// Only explicitly enabled flags are gated here: the account-expiry reminder defaults to enabled, so gating it
        /// would make an otherwise valid wallet-only deployment unbootable.
        /// </description></item>
        /// <item><description>
        /// <c>userDatabasePath</c> and <c>credentialsDatabasePath</c> resolve to the same file. Two EF models migrate
        /// against one SQLite file, which corrupts the schema rather than failing cleanly.
        /// </description></item>
        /// </list>
        /// Blank database paths are deliberately not validated here: the startup path resolution substitutes the
        /// documented <c>./Data/...</c> defaults, so an omitted key is a supported state and not an error.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown before database migration and hosted-service startup when an enabled feature is unusable or a
        /// structurally invalid value is configured. The message names the configuration key and never echoes the
        /// configured value.
        /// </exception>
        /// <example>
        /// <code>
        /// var appConfig = configuration.Get&lt;AppConfig&gt;() ?? new AppConfig();
        /// ConfigurationPreflight.ValidateEnabledFeatures(appConfig);
        /// </code>
        /// </example>
        public static void ValidateEnabledFeatures(AppConfig appConfig)
        {
            ArgumentNullException.ThrowIfNull(appConfig);

            var panelUrlConfigured = !string.IsNullOrWhiteSpace(appConfig.XuiV3ApiBaseUrl);
            var panelUrlUsable = panelUrlConfigured && IsAbsoluteHttpUrl(appConfig.XuiV3ApiBaseUrl);

            // A structurally wrong value is always a mistake, independent of which features are switched on.
            if (panelUrlConfigured && !panelUrlUsable)
            {
                throw new InvalidOperationException(
                    "Configuration value 'xuiV3ApiBaseUrl' must be an absolute HTTP/HTTPS URL. " +
                    "The configured value is not echoed in this message because it may contain a pasted secret.");
            }

            // Only an explicitly enabled feature is gated on the panel URL. The account-expiry reminder defaults to
            // enabled, so gating it here would stop a deployment that intentionally runs without an XUI panel.
            if (appConfig.VolumeExpirationReminderEnabled && !panelUrlUsable)
            {
                throw new InvalidOperationException(
                    "Configuration value 'volumeExpirationReminderEnabled' is enabled but 'xuiV3ApiBaseUrl' is missing " +
                    "or is not an absolute HTTP/HTTPS URL. Set a valid panel base URL or disable the reminder.");
            }

            if (!string.IsNullOrWhiteSpace(appConfig.UserDatabasePath) &&
                !string.IsNullOrWhiteSpace(appConfig.CredentialsDatabasePath) &&
                string.Equals(appConfig.UserDatabasePath, appConfig.CredentialsDatabasePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Configuration values 'userDatabasePath' and 'credentialsDatabasePath' must point to different " +
                    "files; both currently resolve to the same database file.");
            }
        }

        /// <summary>
        /// Builds the sanitized startup report for configuration that is degraded but not fatal.
        /// </summary>
        /// <param name="globalLoggerChannel">
        /// Global <c>loggerChannel</c> configuration value, the legacy fallback for the central Telegram log channel.
        /// May be null, blank, or malformed.
        /// </param>
        /// <param name="defaultBotLoggerChannel">
        /// Default owned bot's per-bot logger channel, which has the highest precedence because every operational log
        /// is routed through that bot. May be null.
        /// </param>
        /// <param name="globalBackupChannel">
        /// Global <c>backupChannel</c> configuration value. The numeric sentinel <c>0</c> means "not configured" and
        /// is reported as missing rather than as malformed.
        /// </param>
        /// <param name="defaultBotBackupChannel">
        /// Default owned bot's per-bot backup channel, used when no global backup channel resolves. May be null.
        /// </param>
        /// <param name="xuiV3ApiBaseUrl">
        /// Configured XUI v3 panel base URL. Only its presence is inspected here; a non-blank value that is
        /// structurally wrong has already been rejected by <see cref="ValidateEnabledFeatures"/>.
        /// </param>
        /// <returns>
        /// Operator-facing lines that are safe to print to the console, the journal, or a container log. The first line
        /// is always a one-line sanitized summary; each further line describes one degraded area. Never null and never
        /// empty.
        /// </returns>
        /// <remarks>
        /// Callers must print these lines and continue starting up. Treating them as fatal would convert a logging or
        /// panel misconfiguration into a customer-visible outage, which is exactly what the warn-only policy prevents.
        ///
        /// The method performs no I/O, makes no network request, and never echoes a configured value.
        /// </remarks>
        /// <example>
        /// <code>
        /// foreach (var line in ConfigurationPreflight.DescribeStartupReport(
        ///     configuration["loggerChannel"],
        ///     botRegistry.DefaultBot?.LoggerChannel,
        ///     configuration["backupChannel"],
        ///     botRegistry.DefaultBot?.BackupChannel,
        ///     appConfig.XuiV3ApiBaseUrl))
        /// {
        ///     Console.WriteLine(line);
        /// }
        /// </code>
        /// </example>
        public static IReadOnlyList<string> DescribeStartupReport(
            string globalLoggerChannel,
            string defaultBotLoggerChannel,
            string globalBackupChannel,
            string defaultBotBackupChannel,
            string xuiV3ApiBaseUrl)
        {
            // The default owned bot wins because it is the sender of every operational log; the global key remains the
            // legacy fallback for a deployment with no per-bot channel configured. Resolution validates, so a
            // malformed higher-precedence value can never mask a correct lower-precedence one.
            var loggerChannel = TelegramDestination.SelectValid(defaultBotLoggerChannel, globalLoggerChannel);
            var backupChannel = TelegramDestination.SelectValid(defaultBotBackupChannel, globalBackupChannel);

            var lines = new List<string>
            {
                $"[ConfigurationPreflight] loggerChannel={Describe(loggerChannel)} backupChannel={Describe(backupChannel)}"
            };

            if (loggerChannel.Length == 0)
            {
                lines.Add(
                    "[ConfigurationPreflight] warning: no deliverable Telegram logger channel is configured, so durable " +
                    "Payment/Html audit rows are not queued and routine Telegram logs are dropped. " +
                    $"botLoggerChannel={DescribeReason(defaultBotLoggerChannel)} globalLoggerChannel={DescribeReason(globalLoggerChannel)}. " +
                    "Set the global loggerChannel (or the default owned bot logger channel) to a numeric chat id such as " +
                    "-1001234567890 or to an @username. Payment settlement, XUI operations and customer handling continue unaffected.");
            }

            if (backupChannel.Length == 0)
            {
                lines.Add(
                    "[ConfigurationPreflight] warning: no deliverable Telegram backup channel is configured, so database " +
                    "backup generations stay pending. " +
                    $"botBackupChannel={DescribeReason(defaultBotBackupChannel)} globalBackupChannel={DescribeReason(globalBackupChannel)}. " +
                    "Set the global backupChannel (or the default owned bot backup channel) to a numeric chat id or an " +
                    "@username. A value of 0 means 'not configured'. Log delivery continues unaffected.");
            }

            if (string.IsNullOrWhiteSpace(xuiV3ApiBaseUrl))
            {
                lines.Add(
                    "[ConfigurationPreflight] warning: xuiV3ApiBaseUrl is not configured, so XUI v3 customer flows " +
                    "(account creation, renewal, link change), the account-expiry reminder, and volume-expiration " +
                    "reminders fail at runtime until it is set. Existing wallet balances and payment settlement are unaffected.");
            }

            return lines;
        }

        /// <summary>
        /// Describes one raw Telegram destination as a non-secret kind label.
        /// </summary>
        /// <param name="value">Raw configured destination text; may be null, blank, or malformed.</param>
        /// <returns><c>chat-id</c>, <c>username</c>, or <c>none</c>. Never the destination text itself.</returns>
        private static string Describe(string value) => TelegramDestination.Classify(value) switch
        {
            TelegramDestinationKind.ChatId => "chat-id",
            TelegramDestinationKind.Username => "username",
            _ => "none"
        };

        /// <summary>
        /// Describes why one raw Telegram destination was rejected, using a closed-vocabulary code only.
        /// </summary>
        /// <param name="value">Raw configured destination text that may or may not be valid.</param>
        /// <returns>
        /// <c>ok</c> when the value is deliverable; otherwise <c>blank</c>, <c>zero</c>, or <c>malformed</c>. The
        /// rejected text itself is never included, because it may be arbitrary input or a pasted secret.
        /// </returns>
        private static string DescribeReason(string value) =>
            TelegramDestination.TryNormalize(value, out _, out var reason) ? "ok" : reason;

        /// <summary>
        /// Checks whether a configured endpoint is an absolute HTTP or HTTPS URL.
        /// </summary>
        /// <param name="value">Raw configuration URL; null, blank, and relative values are invalid.</param>
        /// <returns><c>true</c> for absolute HTTP/HTTPS URLs; otherwise <c>false</c>.</returns>
        private static bool IsAbsoluteHttpUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
