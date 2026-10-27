using Adminbot.Domain;

/// <summary>Owned administrator profile lookup using existing global credentials and exact local Telegram references.</summary>
public partial class TelegramBotService
{
    /// <summary>Builds the existing administrator profile response from a numeric id or exactly observed Telegram username.</summary>
    /// <param name="input">Untrusted text from the already authorized See User Account step; supports id, username, @username and HTTP/HTTPS t.me profile URL.</param>
    /// <param name="cancellationToken">Current bot/user lane lifetime for local username lookup and optional display-only website enrichment.</param>
    /// <returns>The same escaped profile as numeric-id lookup, or a fixed Persian invalid/not-found/ambiguous notice with no raw input or database error.</returns>
    /// <remarks>Only the configured-super-admin dispatcher calls this private helper. Global profile scope matches existing numeric lookup;
    /// tenant ownership and username knowledge confer no access. Duplicate historical usernames require a numeric id, never FirstOrDefault.
    /// No Telegram username resolution, wallet mutation or order is performed. The existing profile renderer may refresh profile fields
    /// and enrich the colleague website balance with its bounded read-only lookup; block and role flags remain authoritative.</remarks>
    /// <example><code>var text = await BuildAdminUserAccountLookupMessageAsync("@im_Tony2", cancellationToken);</code></example>
    private async Task<string> BuildAdminUserAccountLookupMessageAsync(string input, CancellationToken cancellationToken)
    {
        if (!TelegramUserAccountReference.TryParse(input, out var userId, out var username))
            return "ورودی نامعتبر است. آیدی عددی، یوزرنیم تلگرام یا لینک پروفایل t.me را ارسال کنید.";

        CredUser target;
        if (userId > 0)
            target = await _credentialsDbContext.GetUserStatusWithId(userId);
        else
        {
            var matches = await _credentialsDbContext.FindUsersByUsernameAsync(username, cancellationToken);
            if (matches.Count > 1)
                return "این یوزرنیم در سوابق چند کاربر ثبت شده است. برای نمایش مشخصات دقیق، آیدی عددی کاربر را ارسال کنید.";
            target = matches.SingleOrDefault();
        }

        return target == null
            ? "کاربر با این مشخصات پیدا نشد. کاربر باید قبلاً ربات را اجرا کرده باشد؛ یوزرنیم ذخیره‌شده پس از تعامل بعدی او به‌روز می‌شود."
            : await GetUserProfileMessage(target, cancellationToken);
    }
}
