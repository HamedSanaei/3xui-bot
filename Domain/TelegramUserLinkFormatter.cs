using System.Linq;
using System.Net;

namespace Adminbot.Domain
{
    public static class TelegramUserLinkFormatter
    {
        public static string HtmlUserLink(CredUser user)
        {
            if (user == null)
                return string.Empty;

            var label = BuildDisplayName(user);
            return $"<a href=\"tg://user?id={user.TelegramUserId}\">{Html(label)}</a>";
        }

        public static string HtmlUsername(CredUser user)
        {
            if (string.IsNullOrWhiteSpace(user?.Username))
                return "ندارد";

            var username = user.Username.Trim().TrimStart('@');
            return $"@{Html(username)}";
        }

        public static string HtmlSummary(CredUser user)
        {
            if (user == null)
                return string.Empty;

            return $"👤 نام: {HtmlUserLink(user)}\n" +
                   $"🔹 یوزرنیم: {HtmlUsername(user)}\n" +
                   $"🆔 آیدی عددی: <code>{user.TelegramUserId}</code>\n" +
                   $"👥 نوع: <code>{Html(user.IsColleague ? "همکار" : "کاربر عادی")}</code>";
        }

        private static string BuildDisplayName(CredUser user)
        {
            var fullName = string.Join(" ", new[] { user.FirstName, user.LastName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));

            if (!string.IsNullOrWhiteSpace(fullName))
                return fullName;

            if (!string.IsNullOrWhiteSpace(user.Username))
                return "@" + user.Username.Trim().TrimStart('@');

            return user.TelegramUserId.ToString();
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
