using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>Private-chat super-admin control plane for business recovery linked to terminal inbox receipts.</summary>
/// <remarks>Receivers await this bounded management path directly so full inboxes cannot hide the repair
/// surface. Commands never replay customer handlers. One global gate bounds review I/O across every bot.</remarks>
public sealed class TelegramInboxAdminService
{
    private readonly TelegramUpdateInboxStore _inbox;
    private readonly UserDbContextFactory _users;
    private readonly AppConfig _config;
    private readonly IConfiguration _configuration;
    private readonly BotRegistry _bots;
    private readonly IServiceScopeFactory _services;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the operator service using factories and trusted global authorization settings.</summary>
    /// <param name="inbox">Durable inbox and conservative review preconditions.</param>
    /// <param name="users">users.db factory; no context survives a network call.</param>
    /// <param name="config">Global super-admin Telegram id allowlist, never tenant admin ids.</param>
    /// <param name="configuration">Private panel transport configuration, never returned in command responses.</param>
    /// <param name="bots">Runtime registry identifying the default-owned control bot.</param>
    /// <param name="services">
    /// Scope factory used only for the explicit tenant-order recovery command. Tests that exercise inspection only may
    /// omit it; production dependency injection always supplies it.
    /// </param>
    public TelegramInboxAdminService(TelegramUpdateInboxStore inbox, UserDbContextFactory users, AppConfig config,
        IConfiguration configuration, BotRegistry bots, IServiceScopeFactory services = null)
    {
        _inbox = inbox; _users = users; _config = config; _configuration = configuration; _bots = bots;
        _services = services;
    }

    /// <summary>Authorizes only global super-admins in a private chat through the default-owned bot.</summary>
    /// <param name="botId">Internal receiving bot id, supplied by its receiver runtime.</param>
    /// <param name="actor">Telegram From.Id supplied by Telegram; never a command argument.</param>
    /// <param name="privateChat">Whether Telegram identifies this as a private chat.</param>
    /// <returns>True when all control-plane authorization checks pass.</returns>
    /// <remarks>Tenant owners and group chats cannot inspect private operational evidence.</remarks>
    public bool IsAuthorized(string botId, long actor, bool privateChat) => privateChat && actor > 0
        && _config.AdminsUserIds?.Contains(actor) == true && _bots.DefaultBot?.Id == botId
        && _bots.DefaultBot.Type != BotInstanceTypes.Tenant;

    /// <summary>Handles reserved inbox management commands before normal durable admission.</summary>
    /// <param name="botId">Trusted receiver bot identity.</param>
    /// <param name="client">Current Telegram v19 client, used only outside SQLite operations.</param>
    /// <param name="update">Private receiver update; only command arguments are parsed, never logged.</param>
    /// <param name="token">Tracked receiver cancellation; also bounds all admin network/database work.</param>
    /// <returns>True for a recognized management command, including denied attempts; false for normal updates.</returns>
    /// <remarks>Unauthorized commands produce no evidence. A nonblocking gate rejects concurrent management work.
    /// A thirty-second operation deadline keeps this receiver-owned control path bounded.</remarks>
    public async Task<bool> TryHandleAsync(string botId, ITelegramBotClient client, Update update, CancellationToken token)
    {
        var message = update.Message;
        var parts = message?.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts?.FirstOrDefault()?.Split('@')[0];
        if (command is not ("/inbox_uncertain" or "/inbox_resolve" or "/inbox_reconcile" or
            "/inbox_reject_creation" or "/inbox_retry_tenant_order")) return false;
        if (!IsAuthorized(botId, message.From?.Id ?? 0, message.Chat.Type == ChatType.Private)) return true;
        if (!await _gate.WaitAsync(0, token)) return true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            string response;
            try { response = await ExecuteAuthorizedAsync(botId, message.From.Id, true, parts, deadline.Token); }
            catch (ArgumentException) { response = "Invalid arguments. Use /inbox_resolve, /inbox_reject_creation, or /inbox_retry_tenant_order SEQUENCE review-N."; }
            catch (Exception) when (!token.IsCancellationRequested) { response = "Review unavailable; no unsafe resolution was authorized. Inspect again."; }
            await client.SendTextMessageAsync(message.Chat.Id, response, cancellationToken: deadline.Token);
        }
        finally { _gate.Release(); }
        return true;
    }

    /// <summary>Checks global super-admin authority before reading or resolving any recovery evidence.</summary>
    /// <param name="botId">Trusted runtime bot id.</param>
    /// <param name="actor">Telegram-authenticated From.Id, never a command argument.</param>
    /// <param name="privateChat">Telegram private-chat flag; required for sensitive operational metadata.</param>
    /// <param name="parts">Restricted management command tokens, never logged.</param>
    /// <param name="token">Bounded caller lifetime cancellation.</param>
    /// <returns>Fixed denied response without database access, or sanitized authorized command output.</returns>
    /// <remarks>The receiver adapter bounds concurrent calls. No original update handler is ever invoked.</remarks>
    internal Task<string> ExecuteAuthorizedAsync(string botId, long actor, bool privateChat, string[] parts, CancellationToken token) =>
        IsAuthorized(botId, actor, privateChat) ? ExecuteAsync(actor, parts, token) : Task.FromResult("Denied.");

    /// <summary>Executes one already authenticated, serialized operator command with sanitized responses.</summary>
    /// <param name="actor">Authenticated global super-admin Telegram id, not user-entered text.</param>
    /// <param name="parts">Command tokens; only numeric sequences/cursors and review-N tickets are accepted.</param>
    /// <param name="token">Thirty-second management deadline.</param>
    /// <returns>Bounded plain-text metadata or a fixed refusal; no private keys, payloads or panel results.</returns>
    /// <remarks>Inspection is read-only. Reconcile performs identity-safe GET and positive-proof persistence only.
    /// Resolution always requires a separate explicit command and never replays the original update.</remarks>
    private async Task<string> ExecuteAsync(long actor, string[] parts, CancellationToken token)
    {
        var command = parts[0].Split('@')[0];
        if (command == "/inbox_uncertain" && (parts.Length == 1 || parts.Length == 3 && parts[1] == "page"))
        {
            var after = parts.Length == 1 ? 0 : long.Parse(parts[2]);
            var rows = (await _inbox.ListUncertainAsync(after, token)).Take(5).ToList();
            var summaries = new List<string>();
            foreach (var entry in rows)
                summaries.Add(Format(entry) + "\n" + (await _inbox.CanResolveAsync(entry.Sequence, token)).Evidence);
            return rows.Count == 0 ? "No uncertain updates." : string.Join("\n\n", summaries)
                + $"\nNext: /inbox_uncertain page {rows[^1].Sequence}\nInspect: /inbox_uncertain SEQUENCE";
        }
        if (parts.Length < 2 || !long.TryParse(parts[1], out var sequence) || sequence <= 0) throw new ArgumentException();
        if (command == "/inbox_resolve")
        {
            if (parts.Length != 3) throw new ArgumentException();
            var evidence = await _inbox.CanResolveAsync(sequence, token);
            if (!evidence.Allowed) return "Resolution refused: " + evidence.Evidence;
            return await _inbox.ResolveReviewedAsync(sequence, actor, parts[2], token)
                ? "Reviewed and completed; original handler was not replayed." : "Resolution refused; inspect current evidence.";
        }
        if (command == "/inbox_reject_creation")
        {
            if (parts.Length != 3)
                throw new ArgumentException();
            return await RejectAbsentCreationsAsync(sequence, actor, parts[2], token);
        }
        if (command == "/inbox_retry_tenant_order")
        {
            if (parts.Length != 3)
                throw new ArgumentException();
            if (_services == null)
                return "Tenant retry unavailable.";
            ValidateReviewReference(parts[2]);
            using (TelegramUpdateExecutionScope.Push(sequence))
            {
                await using var scope = _services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<TenantBotService>()
                    .RetryPaidUnfulfilledPurchaseAsync(sequence, actor, parts[2], token);
                return result;
            }
        }
        if (parts.Length != 2) throw new ArgumentException();
        if (command == "/inbox_reconcile") await ReconcileCreationsAsync(sequence, token);
        var row = (await _inbox.ListUncertainAsync(sequence - 1, token)).FirstOrDefault(x => x.Sequence == sequence);
        if (row == null) return "Not uncertain.";
        var check = await _inbox.CanResolveAsync(sequence, token);
        return Format(row) + "\n" + check.Evidence + $"\nEligibleForExplicitReview={check.Allowed}";
    }

    /// <summary>
    /// Proves exact reserved identities absent through authenticated XUI GET responses and records reviewed rejection.
    /// </summary>
    /// <param name="sequence">Internal uncertain inbox sequence supplied by the operator command.</param>
    /// <param name="actor">Authenticated global super-admin Telegram id.</param>
    /// <param name="reviewReference">Restricted <c>review-N</c> audit reference.</param>
    /// <param name="token">Bounded command cancellation for reads and short local writes.</param>
    /// <returns>A sanitized count of rejected creation rows, or a fixed inconclusive/refusal reason.</returns>
    /// <remarks>
    /// Emails come only from persisted ClientJson and never from command text. The method uses only exact GET and
    /// changes state only for an authenticated HTTP response whose API envelope says <c>Obtain (record not found)</c>.
    /// Timeout, authentication failure, malformed data, 5xx, and unknown provider messages leave state unchanged.
    /// </remarks>
    private async Task<string> RejectAbsentCreationsAsync(
        long sequence,
        long actor,
        string reviewReference,
        CancellationToken token)
    {
        ValidateReviewReference(reviewReference);
        if (_inbox.Executing.ContainsKey(sequence))
            return "Creation rejection refused: handler_still_active.";

        TelegramUpdateInboxEntry inbox;
        List<XuiV3CreationOperation> rows;
        await using (var db = _users.CreateDbContext())
        {
            inbox = await db.TelegramUpdateInbox.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Sequence == sequence &&
                    (x.Status == "completed_with_review" || x.Status == "uncertain"), token);
            if (inbox == null)
                return "Creation rejection refused: not_uncertain.";
            rows = await db.XuiV3CreationOperations.AsNoTracking()
                .Where(x => x.InboxSequence == sequence &&
                    (x.Outcome == XuiV3CreationOutcome.Ambiguous || x.Outcome == XuiV3CreationOutcome.PostStarted))
                .Take(20).ToListAsync(token);
        }
        if (rows.Count == 0)
            return "Creation rejection refused: no_unresolved_creation.";

        var panel = BuildConfiguredPanel();
        var panelKey = XuiV3LinkChangeOperationStore.BuildPanelKey(panel);
        if (rows.Any(x => x.PanelKey != panelKey))
            return "Creation rejection refused: configured_panel_mismatch.";

        var store = new XuiV3CreationOperationStore(_users);
        var rejected = 0;
        foreach (var row in rows)
        {
            XuiV3ClientPayload reserved;
            try { reserved = JsonConvert.DeserializeObject<XuiV3ClientPayload>(row.ClientJson); }
            catch (JsonException) { return "Creation rejection inconclusive: invalid_reserved_identity."; }
            if (string.IsNullOrWhiteSpace(reserved?.Email))
                return "Creation rejection inconclusive: invalid_reserved_identity.";

            XuiV3ApiResponse<XuiV3Client> response;
            try { response = await ApiServicev3.GetClientAsync(panel, _configuration, reserved.Email, token); }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return "Creation rejection inconclusive: panel_read_failed.";
            }
            if (!IsAuthoritativeExactNotFound(response))
                return response.Success
                    ? "Creation rejection refused: reserved_identity_exists. Use /inbox_reconcile."
                    : "Creation rejection inconclusive: panel_response_not_authoritative.";

            if (await store.MarkOperatorProvenAbsentAsync(
                sequence, row.OperationKey, actor, reviewReference, token))
                rejected++;
        }
        return rejected == rows.Count
            ? $"Creation absence reviewed; definitiveRejected={rejected}. Original handler was not replayed."
            : "Creation rejection changed concurrently; inspect current evidence.";
    }

    /// <summary>Builds the single configured XUI v3 descriptor without exposing its credentials.</summary>
    /// <returns>Authenticated panel descriptor used only by read-only reconciliation requests.</returns>
    /// <exception cref="InvalidOperationException">The global XUI v3 endpoint is not configured.</exception>
    private ServerInfo BuildConfiguredPanel()
    {
        if (string.IsNullOrWhiteSpace(_config.XuiV3ApiBaseUrl))
            throw new InvalidOperationException("Configured XUI v3 panel is unavailable.");
        return new ServerInfo { ApiVersion = "v3", Url = _config.XuiV3ApiBaseUrl,
            RootPath = _config.XuiV3ApiRootPath, ApiToken = _config.XuiV3ApiToken };
    }

    /// <summary>Recognizes the authenticated XUI v3 exact-detail absence contract.</summary>
    /// <param name="response">Parsed HTTP-success API envelope returned by exact client-detail GET.</param>
    /// <returns>True only for <c>success=false</c> and the exact documented record-not-found message.</returns>
    /// <remarks>HTTP exceptions never reach this method and therefore cannot prove absence.</remarks>
    internal static bool IsAuthoritativeExactNotFound(XuiV3ApiResponse<XuiV3Client> response) =>
        response != null && !response.Success && response.Obj == null &&
        string.Equals(response.Msg?.Trim(), "Obtain (record not found)", StringComparison.Ordinal);

    /// <summary>Validates a non-secret operator review ticket before any panel request.</summary>
    /// <param name="reference">Required <c>review-N</c> reference with one to twelve digits.</param>
    /// <exception cref="ArgumentException">The reference permits arbitrary or oversized input.</exception>
    private static void ValidateReviewReference(string reference)
    {
        if (reference == null ||
            !System.Text.RegularExpressions.Regex.IsMatch(reference, @"\Areview-[0-9]{1,12}\z"))
            throw new ArgumentException("A review-N reference is required.");
    }

    /// <summary>Builds safe operational metadata without reading or displaying the update payload.</summary>
    /// <param name="row">Detached metadata-only uncertain row.</param>
    /// <returns>Bounded plain-text identifiers, coarse category and UTC timing.</returns>
    /// <remarks>Unknown historical failure strings are replaced; no arbitrary database text is reflected.</remarks>
    private static string Format(TelegramUpdateInboxEntry row)
    {
        var bot = new string((row.BotId ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());
        var failure = row.FailureCode is "process_interrupted" or "creation_requires_review" or "execution_failed" or "execution_cancelled"
            ? row.FailureCode : "other";
        var type = Enum.TryParse<UpdateType>(row.UpdateType, out var parsed) && Enum.IsDefined(parsed) ? parsed.ToString() : "Unknown";
        return $"Sequence={row.Sequence} BotId={bot} TelegramUserId={row.TelegramUserId} UpdateId={row.UpdateId} Type={type} Failure={failure}"
            + $"\nAcceptedUtc={row.AcceptedAtUtc:O} StartedUtc={row.StartedAtUtc:O} AgeSeconds={Math.Max(0, (DateTime.UtcNow-row.AcceptedAtUtc).TotalSeconds):F0}";
    }

    /// <summary>Reconciles linked creation identities against the configured matching panel using GET only.</summary>
    /// <param name="sequence">Internal uncertain inbox sequence.</param>
    /// <param name="token">Management deadline for detached reads, panel GET and proof persistence.</param>
    /// <returns>A task completing after every bounded linked attempt was inspected; absence never grants POST.</returns>
    /// <remarks>No handler or financial mutation is replayed. Existing wallet/renewal/link recovery mechanisms
    /// remain responsible for their effects; CanResolve refuses their unresolved evidence.</remarks>
    private async Task ReconcileCreationsAsync(long sequence, CancellationToken token)
    {
        if (_inbox.Executing.ContainsKey(sequence)) return;
        List<XuiV3CreationOperation> rows;
        await using (var db = _users.CreateDbContext())
            rows = await db.XuiV3CreationOperations.AsNoTracking().Where(x => x.InboxSequence == sequence
                && (x.Outcome == XuiV3CreationOutcome.Ambiguous || x.Outcome == XuiV3CreationOutcome.PostStarted)).Take(20).ToListAsync(token);
        var panel = new ServerInfo { ApiVersion = "v3", Url = _config.XuiV3ApiBaseUrl,
            RootPath = _config.XuiV3ApiRootPath, ApiToken = _config.XuiV3ApiToken };
        if (string.IsNullOrWhiteSpace(panel.Url)) return;
        var store = new XuiV3CreationOperationStore(_users);
        foreach (var row in rows)
        {
            if (XuiV3LinkChangeOperationStore.BuildPanelKey(panel) != row.PanelKey) continue;
            var reserved = JsonConvert.DeserializeObject<XuiV3ClientPayload>(row.ClientJson);
            var response = await ApiServicev3.GetClientAsync(panel, _configuration, reserved.Email, token);
            if (response.Success && ApiServicev3.MatchesReservedCreation(reserved, response.Obj)
                && JsonConvert.DeserializeObject<List<int>>(row.InboundIdsJson).All(id => response.Obj.InboundIds.Contains(id)))
                await store.MarkAppliedAsync(row.OperationKey, token);
        }
    }
}
