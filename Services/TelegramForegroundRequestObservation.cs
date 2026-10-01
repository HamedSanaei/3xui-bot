using System;

/// <summary>Closed diagnostic vocabulary for the existing foreground-bounded Telegram request surface.</summary>
/// <remarks>Only explicit SDK request mappings produce these values; no request content or dynamic type name is retained.</remarks>
public enum TelegramForegroundRequestKind
{
    /// <summary>A text message send.</summary>
    TextSend,
    /// <summary>A text, caption, media, or reply-markup edit.</summary>
    MessageEdit,
    /// <summary>A callback-query acknowledgement.</summary>
    CallbackAcknowledgement,
    /// <summary>A document send using the existing multipart upload budget.</summary>
    DocumentUpload,
    /// <summary>An album send using the existing multipart upload budget.</summary>
    MediaGroup,
    /// <summary>A photo send using the existing ordinary budget.</summary>
    PhotoSend,
    /// <summary>A message deletion.</summary>
    DeleteMessage,
    /// <summary>A chat lookup.</summary>
    ChatLookup,
    /// <summary>A member, member-count, or administrator-list lookup.</summary>
    MembershipLookup
}

/// <summary>Closed diagnostic outcome of one foreground Telegram request, without exception descriptions.</summary>
public enum TelegramForegroundRequestOutcome
{
    /// <summary>The request is still awaited; used only by live snapshots.</summary>
    InProgress,
    /// <summary>The inner client returned its response successfully.</summary>
    Completed,
    /// <summary>The decorator's owned deadline cancelled the call while the caller token remained live.</summary>
    ForegroundBudgetExpired,
    /// <summary>Telegram returned an API error; only its numeric error code is retained.</summary>
    TelegramApiError,
    /// <summary>The call threw cancellation while the caller token was cancelled.</summary>
    CallerCancellation,
    /// <summary>The transport failed or cancelled independently of the caller and the owned deadline.</summary>
    TransportError,
    /// <summary>A different exception propagated unchanged; its type and content are not retained.</summary>
    UnexpectedError
}

/// <summary>Immutable metadata-only request completion or live-watchdog observation.</summary>
/// <param name="Kind">Explicitly mapped foreground operation; never request text or a dynamic type name.</param>
/// <param name="Outcome">Completion classification, or InProgress for a live snapshot.</param>
/// <param name="RequestElapsedMs">Monotonic elapsed milliseconds for this one inner-client attempt.</param>
/// <param name="HandlerElapsedMs">Monotonic elapsed milliseconds since the owning update scope was pushed.</param>
/// <param name="ApiErrorCode">Numeric Telegram API error code, or null for all other outcomes.</param>
/// <param name="RequestCount">Number of foreground request attempts started in this scope, including active calls.</param>
/// <param name="TotalTelegramElapsedMs">Sum of completed durations and active elapsed durations, in milliseconds.</param>
/// <remarks>
/// Concurrent durations are summed, so TotalTelegramElapsedMs can exceed handler wall time. Observations contain no
/// request object, URL, text, callback id, file name, token, exception message, stack trace, or Telegram response.
/// Request-end observers are local telemetry only and must not send operator incidents.
/// </remarks>
public readonly record struct TelegramForegroundRequestObservation(
    TelegramForegroundRequestKind Kind,
    TelegramForegroundRequestOutcome Outcome,
    double RequestElapsedMs,
    double HandlerElapsedMs,
    int? ApiErrorCode,
    long RequestCount,
    double TotalTelegramElapsedMs);
