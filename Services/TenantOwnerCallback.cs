using System.Globalization;
using System.Text;
using Adminbot.Domain;

/// <summary>Compact store-addressed owner callbacks with revision and ten-minute expiry.</summary>
/// <remarks>These values are routing data, not authorization. Callers must reload by authenticated owner and store number.</remarks>
public static class TenantOwnerCallback
{
    /// <summary>Wraps a legacy internal action for one explicitly selected store.</summary>
    /// <param name="store">Detached authenticated store; requires a positive stable store number.</param>
    /// <param name="action">Internal TBM action, without secrets. Gateway revisions are replaced by the current envelope.</param>
    /// <returns>UTF-8 callback data of at most 64 bytes.</returns>
    /// <exception cref="ArgumentException">The store/action is missing or invalid, or the action exceeds Telegram's payload limit.</exception>
    /// <example><code>var data = TenantOwnerCallback.Encode(store, "TBM:panel");</code></example>
    public static string Encode(BotInstance store, string action)
    {
        if (store?.TenantStoreNumber is not > 0 || string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("A numbered store and owner action are required.");
        action = action.StartsWith("TBM:", StringComparison.Ordinal) ? action[4..] : action;
        var parts = action.Split(':');
        if (parts[0] == "set-setting") action = $"s:{parts[1]}:{parts[2]}";
        if (parts[0] == "set-enabled") action = $"e:{parts[1]}";
        if (parts[0] == "broadcast-send") action = "b:" + parts[1];
        var issued = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300 * 300).ToString("X", CultureInfo.InvariantCulture);
        var data = $"TBM:{store.TenantStoreNumber:X}:{(store.UpdatedAtUtc ?? store.CreatedAtUtc).Ticks:X}:{issued}:{action}";
        if (Encoding.UTF8.GetByteCount(data) > 64) throw new ArgumentException("Owner callback exceeds 64 bytes.", nameof(action));
        return data;
    }

    /// <summary>Parses an addressed callback and expands its internal action without granting access.</summary>
    /// <param name="data">Untrusted Telegram callback text.</param>
    /// <param name="number">Receives the positive owner-scoped storefront number.</param>
    /// <param name="revision">Receives persisted store revision ticks.</param>
    /// <param name="action">Receives the internal action only when the envelope is valid and fresh.</param>
    /// <returns>False for legacy, malformed, oversized or expired buttons.</returns>
    public static bool TryDecode(string data, out int number, out long revision, out string action)
    {
        number = 0; revision = 0; action = null;
        if (data == null || Encoding.UTF8.GetByteCount(data) > 64) return false;
        var parts = data.Split(':', 5);
        if (parts.Length != 5 || parts[0] != "TBM" || !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) || number <= 0
            || !long.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out revision)
            || !long.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var issued)) return false;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued;
        if (issued < 0 || age < -60 || age > 600) return false;
        action = parts[4];
        if (action.StartsWith("s:", StringComparison.Ordinal)) action = $"set-setting:{action[2..]}:{parts[2]}:{parts[3]}";
        else if (action.StartsWith("e:", StringComparison.Ordinal)) action = $"set-enabled:{action[2..]}:{parts[2]}:{parts[3]}";
        else if (action.StartsWith("b:", StringComparison.Ordinal)) action = "broadcast-send:" + action[2..];
        return true;
    }
}
