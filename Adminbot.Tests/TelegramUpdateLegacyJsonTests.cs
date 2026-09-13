using Adminbot.Domain;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Claims a persisted v19 update through the v22 inbox without losing callback routing or nested Telegram data.</summary>
    /// <returns>A task completing after deserialization and the durable queued-to-running transition are verified.</returns>
    /// <remarks>The fixture was emitted by Telegram.Bot 19.0.0.0 using JsonConvert.SerializeObject, as the old inbox did.
    /// It is deliberately inserted directly into temporary SQLite: serializing it with v22 or calling TryAcceptAsync
    /// would only verify a new-version round trip and would miss upgrade incompatibilities.</remarks>
    [Fact]
    public async Task Inbox_claim_deserializes_persisted_TelegramBot_v19_update_json()
    {
        const string legacyJson = """
            {
              "update_id": 190022,
              "callback_query": {
                "id": "legacy-callback",
                "from": {
                  "id": 5000000001,
                  "is_bot": false,
                  "first_name": "کاربر"
                },
                "message": {
                  "message_id": 77,
                  "from": {
                    "id": 5000000001,
                    "is_bot": false,
                    "first_name": "کاربر"
                  },
                  "date": 1788265800,
                  "chat": {
                    "id": 5000000001,
                    "type": "private"
                  },
                  "text": "/start سلام 🌟",
                  "entities": [
                    {
                      "type": "bot_command",
                      "offset": 0,
                      "length": 6
                    }
                  ],
                  "reply_markup": {
                    "inline_keyboard": [
                      [
                        {
                          "text": "آزمایش 🌟",
                          "callback_data": "legacy:confirm"
                        }
                      ]
                    ]
                  }
                },
                "chat_instance": "legacy-chat-instance",
                "data": "legacy:confirm"
              }
            }
            """;
        using var databases = new Databases();
        var acceptedAt = new DateTime(2026, 9, 1, 12, 30, 1, DateTimeKind.Utc);
        var persisted = new TelegramUpdateInboxEntry
        {
            BotId = "legacy-owned", UpdateId = 190022, TelegramUserId = 5000000001,
            UpdateType = "CallbackQuery", Payload = legacyJson, Status = "queued", AcceptedAtUtc = acceptedAt
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramUpdateInbox.Add(persisted);
            await db.SaveChangesAsync();
        }

        // A fresh store/context reads the committed old payload, just as after upgrading and restarting the process.
        var inbox = new TelegramUpdateInboxStore(databases.Users, databases.Credentials);
        var claimed = await inbox.ClaimAsync(persisted.Sequence, default);
        Assert.NotNull(claimed);
        Assert.Equal(persisted.Sequence, claimed.Sequence);
        Assert.Equal(new TelegramUpdateExecutionKey("legacy-owned", 5000000001), claimed.Key);
        Assert.Equal(acceptedAt, claimed.AcceptedAtUtc);
        Assert.Equal(190022, claimed.Update.Id);
        Assert.Equal(UpdateType.CallbackQuery, claimed.Update.Type);
        var callback = Assert.IsType<CallbackQuery>(claimed.Update.CallbackQuery);
        Assert.Equal("legacy-callback", callback.Id);
        Assert.Equal("legacy:confirm", callback.Data);
        Assert.Equal("legacy-chat-instance", callback.ChatInstance);
        Assert.Equal(5000000001, callback.From.Id);
        Assert.Equal("کاربر", callback.From.FirstName);
        Assert.False(callback.From.IsBot);
        var message = Assert.IsType<Message>(callback.Message);
        Assert.Equal(77, message.Id);
        Assert.Equal(5000000001, message.From!.Id);
        Assert.Equal(5000000001, message.Chat.Id);
        Assert.Equal(ChatType.Private, message.Chat.Type);
        Assert.Equal(new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc), message.Date);
        Assert.Equal(DateTimeKind.Utc, message.Date.Kind);
        Assert.Equal("/start سلام 🌟", message.Text);
        var entity = Assert.Single(message.Entities!);
        Assert.Equal(MessageEntityType.BotCommand, entity.Type);
        Assert.Equal(0, entity.Offset);
        Assert.Equal(6, entity.Length);
        var button = Assert.Single(Assert.Single(message.ReplyMarkup!.InlineKeyboard));
        Assert.Equal("آزمایش 🌟", button.Text);
        Assert.Equal("legacy:confirm", button.CallbackData);

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TelegramUpdateInbox.FindAsync(persisted.Sequence);
        Assert.NotNull(row);
        Assert.Equal("running", row.Status);
        Assert.NotNull(row.StartedAtUtc);
        Assert.Equal(legacyJson, row.Payload);
        Assert.Null(await inbox.ClaimAsync(persisted.Sequence, default));
    }
}
