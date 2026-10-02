using Adminbot.Domain;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;

public sealed partial class ConcurrencyTests
{
    /// <summary>Quoted discount never exceeds the actual merchant margin and eligibility uses the gross amount.</summary>
    [Fact]
    public void TenantDiscount_Price_caps_percentage_fixed_and_margin_without_crossing_base_cost()
    {
        var code = new TenantDiscountCode
        {
            Code = "SALE-25", Kind = TenantDiscountKinds.Percent, Scope = TenantDiscountScopes.Both,
            Percent = 25, MaxDiscountToman = 30000, MinimumOrderToman = 200000,
            MaxUses = 2, IsActive = true
        };
        var price = TenantDiscountService.Quote(code, 200000, 150000, TenantDiscountScopes.Purchase);
        Assert.True(price.Success);
        Assert.Equal(new TenantDiscountPrice(200000, 150000, 30000, 170000), price.Value);
        Assert.Equal(TenantDiscountFailure.MinimumOrder,
            TenantDiscountService.Quote(code, 199999, 150000, TenantDiscountScopes.Purchase).Failure);
        code.Kind = TenantDiscountKinds.Fixed;
        code.Percent = null;
        code.FixedAmountToman = 100000;
        code.MaxDiscountToman = null;
        Assert.Equal(50000, TenantDiscountService.Quote(code, 200000, 150000, TenantDiscountScopes.Renew).Value.DiscountAmountToman);
        code.MaxDiscountToman = 20000;
        Assert.Equal(20000, TenantDiscountService.Quote(code, 200000, 150000, TenantDiscountScopes.Renew).Value.DiscountAmountToman);
        Assert.Equal(TenantDiscountFailure.NoAvailableMargin,
            TenantDiscountService.Quote(code, 200000, 200000, TenantDiscountScopes.Purchase).Failure);
        code.Scope = TenantDiscountScopes.Purchase;
        Assert.Equal(TenantDiscountFailure.Scope,
            TenantDiscountService.Quote(code, 200000, 150000, TenantDiscountScopes.Renew).Failure);
    }

    /// <summary>Two customers racing for one code cannot both reserve; disabling or deleting never reprices the winning order.</summary>
    [Fact]
    public async Task TenantDiscount_Admission_reserves_once_and_soft_delete_allows_reuse_without_rewriting_history()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.AddRange(
                new BotInstance { Id = "discount-a", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 101, TenantStoreNumber = 1 },
                new BotInstance { Id = "discount-b", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 202, TenantStoreNumber = 1 });
            await db.SaveChangesAsync();
        }
        var discounts = new TenantDiscountService(databases.Users, new ServiceSalesAvailabilityService(new AppConfig(),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tenant-discount-fixture-policy.json"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceSalesAvailabilityService>.Instance));
        var definition = new TenantDiscountCodeInput(" sale-25 ", TenantDiscountKinds.Percent, TenantDiscountScopes.Both,
            null, 25, 30000, 200000, 1, true);
        var first = await discounts.SaveCodeAsync("discount-a", 101, definition);
        Assert.True(first.Success);
        Assert.Equal("SALE-25", first.Value.Code);
        Assert.Equal(TenantDiscountFailure.DuplicateCode,
            (await discounts.SaveCodeAsync("discount-a", 101, definition with { Code = "SaLe-25" })).Failure);
        Assert.Equal(TenantDiscountFailure.Unauthorized,
            (await discounts.GetCodeAsync("discount-a", 202, first.Value.Id)).Failure);
        Assert.True((await discounts.SaveCodeAsync("discount-b", 202, definition)).Success);
        var quoted = await discounts.QuoteAsync("discount-a", "Sale-25", 200000, 150000, TenantDiscountScopes.Purchase);
        Assert.True(quoted.Success);
        TenantDiscountSelection selection = new(first.Value.Id, first.Value.UpdatedAtUtc, quoted.Value.Price);
        TenantBotOrder Order(long customer) => new()
        {
            OrderId = $"discount-renew-{customer}", TenantBotId = "discount-a", OwnerTelegramUserId = 101,
            CustomerTelegramUserId = customer, CustomerChatId = customer, OrderKind = TenantBotOrderKinds.Renew,
            ServiceKey = "normal", TrafficGb = 10, DurationKey = "m1", AccountCount = 1,
            TargetAccountEmail = $"renew-{customer}", PaymentProvider = "pending"
        };
        var attempts = await Task.WhenAll(
            discounts.AdmitRenewalOrderAsync(Order(301), selection, 200000, 150000),
            discounts.AdmitRenewalOrderAsync(Order(302), selection, 200000, 150000));
        var winner = Assert.Single(attempts, x => x.Success).Value;
        Assert.Equal(TenantDiscountFailure.Exhausted, Assert.Single(attempts, x => !x.Success).Failure);
        Assert.Equal(170000, winner.SalePriceToman);
        Assert.Equal(20000, winner.ProfitToman);
        Assert.Equal(TenantDiscountFailure.InvalidDefinition,
            (await discounts.SaveCodeAsync("discount-a", 101, definition with { MaxUses = 0 }, first.Value.Id, first.Value.UpdatedAtUtc)).Failure);
        Assert.True((await discounts.SetActiveAsync("discount-a", 101, first.Value.Id, false)).Success);
        Assert.True((await discounts.DeleteAsync("discount-a", 101, first.Value.Id)).Success);
        Assert.True((await discounts.SaveCodeAsync("discount-a", 101, definition)).Success);
        await using var check = databases.Users.CreateDbContext();
        var saved = await check.TenantBotOrders.SingleAsync();
        var claim = await check.TenantDiscountRedemptions.SingleAsync();
        Assert.Equal(first.Value.Id, claim.CodeId);
        Assert.Equal(saved.Id, claim.TenantBotOrderId);
        Assert.Equal(TenantDiscountRedemptionStates.Reserved, claim.State);
        Assert.Equal((200000L, 30000L, 170000L),
            (saved.OriginalSalePriceToman!.Value, saved.DiscountAmountToman!.Value, saved.SalePriceToman));
        Assert.Equal("SALE-25", saved.AppliedDiscountCode);
    }
    /// <summary>Owner-only callbacks edit a draft, freeze code edits until save, reject stale buttons and retain soft-deleted claims.</summary>
    [Fact]
    public async Task TenantDiscount_Owner_nested_menu_creates_edits_disables_and_deletes_in_selected_store()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var store = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        var client = new StorefrontClient();
        var state = provider.GetRequiredService<UserStateStore>();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-discount" }, Client = client
        });
        async Task Press(string action)
        {
            await using var db = databases.Users.CreateDbContext();
            var current = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id);
            await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(current, action));
        }
        async Task Enter(string text)
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerMessageAsync(client,
                new Message { From = new Telegram.Bot.Types.User { Id = 711 }, Chat = new Chat { Id = 711 }, Text = text },
                owner, await state.GetUserStatus(711), new ReplyKeyboardRemove(), default);
        }
        await Press("panel");
        Assert.Contains("🎟 کدهای تخفیف", client.Labels);
        await Press("d:l");
        await Press("d:c");
        await Press("d:e:0:code"); await Enter("off-25");
        await Press("d:t:p");
        await Press("d:e:0:value"); await Enter("25");
        await Press("d:e:0:cap"); await Enter("30000");
        await Press("d:e:0:min"); await Enter("200000");
        await Press("d:e:0:limit"); await Enter("1");
        await Press("d:a:b");
        var nonce = (string)JObject.Parse((await state.GetUserStatus(711)).OwnerDiscountDraftJson)["Nonce"]!;
        await Press($"d:save:{nonce}");
        var codes = provider.GetRequiredService<TenantDiscountService>();
        var created = (await codes.ListCodesAsync(store.Id, 711)).Value.Single();
        Assert.Equal(("OFF-25", 30000L, TenantDiscountScopes.Both), (created.Code, created.MaxDiscountToman!.Value, created.Scope));
        await Press($"d:e:{created.Id}:menu");
        await Press($"d:e:{created.Id}:cap"); await Enter("20000");
        // The customer quote still sees the live 30k cap until the owner saves.
        Assert.Equal(30000, (await codes.QuoteAsync(store.Id, "OFF-25", 200000, 150000, TenantDiscountScopes.Purchase)).Value.Price.DiscountAmountToman);
        nonce = (string)JObject.Parse((await state.GetUserStatus(711)).OwnerDiscountDraftJson)["Nonce"]!;
        await Press($"d:save:{nonce}");
        Assert.Equal(20000, (await codes.QuoteAsync(store.Id, "OFF-25", 200000, 150000, TenantDiscountScopes.Purchase)).Value.Price.DiscountAmountToman);
        var stale = TenantOwnerCallback.Encode(store, $"d:s:{created.Id}:0");
        await Press($"d:s:{created.Id}:0");
        Assert.False((await codes.GetCodeAsync(store.Id, 711, created.Id)).Value.IsActive);
        await OwnerCallback(provider, client, owner, stale);
        Assert.False((await codes.GetCodeAsync(store.Id, 711, created.Id)).Value.IsActive);
        await Press("panel");
        await Press($"d:s:{created.Id}:1");
        Assert.True((await codes.GetCodeAsync(store.Id, 711, created.Id)).Value.IsActive);
        await Press($"d:x:{created.Id}");
        await Press($"d:xc:{created.Id}");
        Assert.Equal(TenantDiscountFailure.NotFound, (await codes.GetCodeAsync(store.Id, 711, created.Id)).Failure);
        var beforeReset = await codes.SaveCodeAsync(store.Id, 711,
            new TenantDiscountCodeInput("reset-me", TenantDiscountKinds.Fixed, TenantDiscountScopes.Purchase,
                1000, null, null, 0, 2, true));
        Assert.True(beforeReset.Success);
        await Press("reset-confirm");
        Assert.Equal(TenantDiscountFailure.NotFound,
            (await codes.GetCodeAsync(store.Id, 711, beforeReset.Value.Id)).Failure);
        Assert.All(client.Callbacks, data => Assert.InRange(Encoding.UTF8.GetByteCount(data), 1, 64));
    }

    /// <summary>Repeated owner edits complete the durable inbox and cannot mutate the live definition before save.</summary>
    /// <param name="action">A repeatable discount choice from the confirmed incident; the same rendered menu is delivered twice.</param>
    /// <returns>A task after real owner callbacks, persisted inbox outcomes, and unchanged live discount state are checked.</returns>
    /// <remarks>Two distinct updates replay the same choice; redelivery of the identical update id is also deduplicated by the inbox.</remarks>
    [Theory]
    [InlineData("d:a:p")]
    [InlineData("d:t:p")]
    [InlineData("d:t:f")]
    [InlineData("d:cap:none")]
    public async Task TenantDiscount_Repeated_owner_edit_is_successful_and_preserves_draft_and_business_state(string action)
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var store = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var discounts = provider.GetRequiredService<TenantDiscountService>();
        var saved = await discounts.SaveCodeAsync(store.Id, 711,
            new TenantDiscountCodeInput("UNCHANGED", TenantDiscountKinds.Fixed, TenantDiscountScopes.Both,
                1000, null, 3000, 0, 4, true));
        Assert.True(saved.Success);
        store = (await stores.ListAsync(711)).Single();
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        var client = new DiscountEditClient();
        var accessor = provider.GetRequiredService<BotContextAccessor>();
        using var context = accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-discount-replay" }, Client = client
        });
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, $"d:e:{saved.Value.Id}:menu"));
        var callbackData = TenantOwnerCallback.Encode(store, action);
        await OwnerCallback(provider, client, owner, callbackData);
        var state = provider.GetRequiredService<UserStateStore>();
        var expectedDraft = (await state.GetUserStatus(711)).OwnerDiscountDraftJson;
        var expectedRevision = saved.Value.UpdatedAtUtc;
        var executed = new List<int>();
        using var scheduler = Create(databases, new Executor(async (item, token) =>
        {
            using var executionContext = accessor.Push(new BotRuntimeContext
            {
                Config = new BotInstanceConfig { Id = item.Key.BotId }, Client = client
            });
            await using var scope = provider.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerCallbackAsync(
                client, item.Update.CallbackQuery!, owner, await state.GetUserStatus(711), token));
            executed.Add(item.Update.Id);
        }));
        var first = DiscountCallbackUpdate(2101, owner, callbackData);
        await scheduler.EnqueueAsync("owned-discount-replay", first, default);
        await scheduler.EnqueueAsync("owned-discount-replay", first, default);
        await scheduler.EnqueueAsync("owned-discount-replay", DiscountCallbackUpdate(2102, owner, callbackData), default);
        await scheduler.StartAsync(default);
        await scheduler.StopAsync(default);

        Assert.Equal(new[] { 2101, 2102 }, executed);
        Assert.Equal(expectedDraft, (await state.GetUserStatus(711)).OwnerDiscountDraftJson);
        Assert.Equal(store.Id, (await state.GetUserStatus(711)).OwnerStoreId);
        Assert.True(client.NoOpEdits >= 2);
        var live = (await discounts.GetCodeAsync(store.Id, 711, saved.Value.Id)).Value;
        Assert.Equal((TenantDiscountKinds.Fixed, TenantDiscountScopes.Both, 1000L, 3000L, 4, expectedRevision),
            (live.Kind, live.Scope, live.FixedAmountToman!.Value, live.MaxDiscountToman!.Value, live.MaxUses, live.UpdatedAtUtc));
        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(new[] { 2101, 2102 }, await db.TelegramUpdateInbox.OrderBy(x => x.Sequence).Select(x => x.UpdateId).ToArrayAsync());
        Assert.All(await db.TelegramUpdateInbox.ToListAsync(), row =>
        {
            Assert.Equal("completed", row.Status);
            Assert.Null(row.FailureCode);
            Assert.Null(row.Payload);
        });
        Assert.Single(await db.TenantDiscountCodes.ToListAsync());
        Assert.Empty(await db.TenantDiscountRedemptions.ToListAsync());
        Assert.Empty(await db.TenantBotOrders.ToListAsync());
        Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
    }

    /// <summary>A genuine edit rejection propagates and remains an execution_failed outcome, not a semantic no-op.</summary>
    /// <param name="code">Telegram numeric status; non-400 statuses must not match even when the description mentions a no-op.</param>
    /// <param name="description">Controlled Telegram error text unrelated to valid identical-edit confirmation.</param>
    /// <returns>A task after direct exception identity and durable failure classification are verified.</returns>
    /// <remarks>Includes missing edit targets, which the legacy best-effort helper swallows but strict discount rendering must expose.</remarks>
    [Theory]
    [InlineData(400, "Bad Request: message to edit not found")]
    [InlineData(400, "Bad Request: can't parse entities")]
    [InlineData(403, "Forbidden: message is not modified")]
    public async Task TenantDiscount_Other_edit_failures_propagate_and_remain_execution_failed(int code, string description)
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var store = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        var client = new DiscountEditClient();
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-discount-rejection" }, Client = client
        });
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "d:c"));
        var data = TenantOwnerCallback.Encode(store, "d:a:p");
        var failure = new ApiRequestException(description, code);
        client.EditFailure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<ApiRequestException>(() => OwnerCallback(provider, client, owner, data)));
        var expectedDraft = (await provider.GetRequiredService<UserStateStore>().GetUserStatus(711)).OwnerDiscountDraftJson;
        using var scheduler = Create(databases, new Executor((item, _) => OwnerCallback(provider, client, owner, item.Update.CallbackQuery!.Data!)));
        await scheduler.EnqueueAsync("owned-discount-rejection", DiscountCallbackUpdate(2201, owner, data), default);
        await scheduler.StartAsync(default);
        await scheduler.StopAsync(default);
        await using var db = databases.Users.CreateDbContext();
        var row = await db.TelegramUpdateInbox.SingleAsync();
        Assert.Equal("completed_with_error", row.Status);
        Assert.Equal("execution_failed", row.FailureCode);
        Assert.Equal(expectedDraft, (await provider.GetRequiredService<UserStateStore>().GetUserStatus(711)).OwnerDiscountDraftJson);
    }

    /// <summary>Outer cancellation wins even if Telegram simultaneously confirms that the requested discount menu is unchanged.</summary>
    /// <returns>A task after cancellation escapes the actual owner callback without committing a live discount.</returns>
    /// <remarks>The fake cancels only at the edit boundary, after state reads and the idempotent draft choice have completed.</remarks>
    [Fact]
    public async Task TenantDiscount_Outer_cancellation_is_not_swallowed_by_no_op_edit()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var store = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        var client = new DiscountEditClient();
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-discount-cancel" }, Client = client
        });
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "d:c"));
        var data = TenantOwnerCallback.Encode(store, "d:a:p");
        await OwnerCallback(provider, client, owner, data);
        using var cancellation = new CancellationTokenSource();
        client.BeforeEdit = cancellation.Cancel;
        await using var scope = provider.CreateAsyncScope();
        var callback = DiscountCallbackUpdate(2301, owner, data).CallbackQuery!;
        var state = await provider.GetRequiredService<UserStateStore>().GetUserStatus(711);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerCallbackAsync(
                client, callback, owner, state, cancellation.Token));
        await using var db = databases.Users.CreateDbContext();
        Assert.Empty(await db.TenantDiscountCodes.ToListAsync());
    }

    /// <summary>Creates a synthetic addressed owner update without sending data to Telegram.</summary>
    /// <param name="id">Distinct numeric Telegram update id for inbox deduplication.</param>
    /// <param name="owner">Authenticated colleague profile whose Telegram sender and private chat ids must match.</param>
    /// <param name="data">Addressed test callback envelope, not a credential or authorization grant.</param>
    /// <returns>A callback update accepted by the real inbox and owner handler.</returns>
    /// <remarks>The existing menu id is fixed so replay targets the same text and keyboard.</remarks>
    /// <example><code>var update = DiscountCallbackUpdate(2101, owner, TenantOwnerCallback.Encode(store, "d:a:p"));</code></example>
    private static Update DiscountCallbackUpdate(int id, CredUser owner, string data) => new()
    {
        Id = id,
        CallbackQuery = new CallbackQuery
        {
            Id = $"discount-{id}", Data = data,
            From = new Telegram.Bot.Types.User { Id = owner.TelegramUserId },
            Message = new Message { Id = 1, Chat = new Chat { Id = owner.TelegramUserId, Type = Telegram.Bot.Types.Enums.ChatType.Private } }
        }
    };

    /// <summary>Emulates Telegram's identical-content edit rejection using the complete rendered text and inline markup.</summary>
    /// <remarks>One owner menu or customer quote is used per fixture. Controlled transport failures never mutate local draft or checkout state themselves.</remarks>
    private sealed class DiscountEditClient : StorefrontClient
    {
        /// <summary>Last acknowledged complete edit payload, including its addressed inline keyboard.</summary>
        private string? _lastEdit;
        /// <summary>Count of rejected identical edits, proving the regression really exercised Telegram's no-op response.</summary>
        public int NoOpEdits { get; private set; }
        /// <summary>Optional controlled rejection returned for every edit.</summary>
        public Exception? EditFailure { get; set; }
        /// <summary>Optional action at the edit boundary used to race outer cancellation with no-op acknowledgement.</summary>
        public Action? BeforeEdit { get; set; }
        /// <inheritdoc />
        public override Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is EditMessageTextRequest edit)
            {
                BeforeEdit?.Invoke();
                if (EditFailure != null) return Task.FromException<TResponse>(EditFailure);
                var render = JsonConvert.SerializeObject(new { edit.ChatId, edit.MessageId, edit.Text, edit.ParseMode, edit.ReplyMarkup });
                if (render == _lastEdit)
                {
                    NoOpEdits++;
                    return Task.FromException<TResponse>(new ApiRequestException(
                        "Bad Request: message is not modified: specified new message content and reply markup are exactly the same", 400));
                }
                _lastEdit = render;
            }
            return base.SendRequest(request, cancellationToken);
        }
    }
    /// <summary>Purchase code entry preserves the message-bound net, including repeated application of the same code.</summary>
    /// <param name="repeatCode">Whether the customer reopens code entry and submits the same valid code again.</param>
    /// <returns>A task after preview invariants and exactly-once discounted payment admission are verified.</returns>
    /// <remarks>Regression: Telegram confirms an identical edit with status 400/message-not-modified; that must not expire or rebind the customer's still-valid quote.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantDiscount_Purchase_preview_applies_code_to_bound_message_without_admission(bool repeatCode)
    {
        using var databases = new Databases();
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        var tenant = new BotInstance
        {
            Id = "tenant-preview", Username = "preview_store", Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 711, TenantStoreNumber = 1, TenantPriceMarkupPercent = 25,
            TenantCardPaymentEnabled = true, TenantCardNumber = "6037991234567890", TenantCardHolderName = "Test",
            Enabled = true
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var discounts = provider.GetRequiredService<TenantDiscountService>();
        Assert.True((await discounts.SaveCodeAsync(tenant.Id, 711,
            new TenantDiscountCodeInput("preview-25", TenantDiscountKinds.Percent, TenantDiscountScopes.Both,
                null, 25, 30000, 0, 1, true))).Success);
        var customer = new CredUser { TelegramUserId = 912 };
        var client = new DiscountEditClient();
        var selection = new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1 };
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client
        });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var preview = typeof(TenantBotService).GetMethod("SHOWCUSTOMERCONFIRMASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)preview.Invoke(service, new object[] { client, new ChatId(912), 912L, null!, tenant, selection, CancellationToken.None })!;
        await using (var db = databases.Users.CreateDbContext())
        {
            var quote = await db.TenantDiscountQuotes.SingleAsync();
            Assert.Equal(1, quote.MessageId);
            Assert.Equal(quote.GrossToman, quote.NetToman);
            Assert.Contains(client.Callbacks, x => x == $"TN:DC:{quote.Id}");
        }
        // Callback identity is taken from the database, not a customer-supplied price or code.
        await using var read = databases.Users.CreateDbContext();
        var selectedQuote = await read.TenantDiscountQuotes.AsNoTracking().SingleAsync();
        var callback = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"), Data = $"TN:DC:{selectedQuote.Id}",
            From = new Telegram.Bot.Types.User { Id = 912 },
            Message = new Message { Id = 1, Chat = new Chat { Id = 912 } }
        };
        var enter = typeof(TenantBotService).GetMethod("HandlePurchaseDiscountCallbackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)enter.Invoke(service, new object[] { client, callback, tenant, customer, $"DC:{selectedQuote.Id}", CancellationToken.None })!;
        var state = await provider.GetRequiredService<UserStateStore>().GetUserStatus(912);
        Assert.Equal("purchase-discount-entry", state.LastStep);
        var text = typeof(TenantBotService).GetMethod("HandlePurchaseDiscountTextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)text.Invoke(service, new object[] { client, new Message
        {
            From = new Telegram.Bot.Types.User { Id = 912 }, Chat = new Chat { Id = 912 }, Text = "PreView-25"
        }, tenant, customer, state, CancellationToken.None })!;
        if (repeatCode)
        {
            // A fresh entry callback is acknowledged before text submission; it cannot be reused for a success popup.
            callback.Id = Guid.NewGuid().ToString("N");
            await (Task)enter.Invoke(service, new object[] { client, callback, tenant, customer, $"DC:{selectedQuote.Id}", CancellationToken.None })!;
            state = await provider.GetRequiredService<UserStateStore>().GetUserStatus(912);
            await (Task)text.Invoke(service, new object[] { client, new Message
            {
                From = new Telegram.Bot.Types.User { Id = 912 }, Chat = new Chat { Id = 912 }, Text = "PreView-25"
            }, tenant, customer, state, CancellationToken.None })!;
            Assert.Equal(1, client.NoOpEdits);
        }
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.TenantDiscountQuotes.SingleAsync();
        Assert.Equal(Math.Min((long)Math.Floor(saved.GrossToman * 0.25M), Math.Min(30000, saved.GrossToman - saved.BaseCostToman)),
            saved.DiscountAmountToman);
        Assert.Equal(saved.GrossToman - saved.DiscountAmountToman, saved.NetToman);
        Assert.Equal(TenantDiscountQuoteStates.Open, saved.State);
        Assert.Empty(await verify.TenantBotOrders.ToListAsync());
        Assert.Empty(await verify.TenantDiscountRedemptions.ToListAsync());
        Assert.Contains(client.Callbacks, x => x == $"TN:DQ:{saved.Id}:CARD");
        var pay = typeof(TenantBotService).GetMethod("HandleQuotedPurchasePaymentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cardCallback = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"), Data = $"TN:DQ:{saved.Id}:CARD",
            From = new Telegram.Bot.Types.User { Id = 912 },
            Message = new Message { Id = saved.MessageId!.Value, Chat = new Chat { Id = 912 } }
        };
        await (Task)pay.Invoke(service, new object[]
        {
            client, cardCallback, tenant, customer, $"DQ:{saved.Id}:CARD", CancellationToken.None
        })!;
        await using (var charged = databases.Users.CreateDbContext())
        {
            var admittedOrder = await charged.TenantBotOrders.AsNoTracking().SingleAsync();
            var claim = await charged.TenantDiscountRedemptions.AsNoTracking().SingleAsync();
            Assert.Equal(saved.NetToman, admittedOrder.SalePriceToman);
            Assert.Equal(saved.GrossToman, admittedOrder.OriginalSalePriceToman);
            Assert.Equal(saved.DiscountAmountToman, admittedOrder.DiscountAmountToman);
            Assert.Equal(admittedOrder.SalePriceToman - admittedOrder.BaseCostToman, admittedOrder.ProfitToman);
            Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, admittedOrder.PaymentStatus);
            Assert.Equal(TenantDiscountRedemptionStates.Reserved, claim.State);
            Assert.Equal(admittedOrder.Id, claim.TenantBotOrderId);
        }
        var definition = await discounts.GetCodeAsync(tenant.Id, 711, (int)saved.CodeId!);
        Assert.True((await discounts.SetActiveAsync(tenant.Id, 711, definition.Value.Id, false)).Success);
        await (Task)pay.Invoke(service, new object[]
        {
            client, cardCallback, tenant, customer, $"DQ:{saved.Id}:CARD", CancellationToken.None
        })!;
        Assert.Equal(TenantDiscountFailure.Conflict,
            (await discounts.AdmitQuotedOrderAsync(saved.Id, tenant.Id, 912, 912, saved.MessageId.Value,
                saved.SelectionKey, "HP", saved.GrossToman, saved.BaseCostToman, null)).Failure);
        await using var replay = databases.Users.CreateDbContext();
        Assert.Equal(1, await replay.TenantBotOrders.CountAsync());
        Assert.Equal(1, await replay.TenantDiscountRedemptions.CountAsync());
    }
    /// <summary>A saved renewal discount is not silently treated as gross after a store, customer, selection or tariff change.</summary>
    [Fact]
    public void TenantDiscount_Renewal_snapshot_rejects_stale_identity_and_renders_actual_net()
    {
        var selection = new XuiV3PurchaseSelection
        {
            ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1
        };
        var buildKey = typeof(TenantBotService).GetMethod("BUILDPAYACTION", BindingFlags.Static | BindingFlags.NonPublic)!;
        var key = (string)buildKey.Invoke(null, new object[] { selection })!;
        var tenant = new BotInstance { Id = "tenant-renew-discount", Type = BotInstanceTypes.Tenant };
        var user = new User { Id = 912, RenewalDiscountSelectionJson = JsonConvert.SerializeObject(new
        {
            TenantBotId = tenant.Id, CustomerTelegramUserId = 912, CodeId = 27,
            CodeUpdatedAtUtc = DateTime.UtcNow, SelectionKey = key,
            DisplayedGrossToman = 200000, DisplayedBaseCostToman = 150000,
            DisplayedDiscountToman = 30000, DisplayedNetToman = 170000
        }) };
        var read = typeof(TenantBotService).GetMethod("ReadTenantRenewDiscountSelection",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        TenantDiscountResult<TenantDiscountSelection> Check(User state, BotInstance store, long gross) =>
            (TenantDiscountResult<TenantDiscountSelection>)read.Invoke(null, new object[] { state, store, selection, gross, 150000L })!;
        var current = Check(user, tenant, 200000);
        Assert.True(current.Success);
        Assert.Equal(170000, current.Value.Displayed.NetToman);
        var render = typeof(TenantBotService).GetMethod("BuildTenantRenewDiscountPriceText",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var text = (string)render.Invoke(null, new object[] { current.Value })!;
        Assert.Contains("170٬000", text);
        Assert.Contains("30٬000", text);
        Assert.DoesNotContain("150٬000", text);
        Assert.Equal(TenantDiscountFailure.ChangedQuote, Check(user, tenant, 200001).Failure);
        Assert.Equal(TenantDiscountFailure.ChangedQuote, Check(user, new BotInstance { Id = "sibling" }, 200000).Failure);
        Assert.Equal(TenantDiscountFailure.ChangedQuote, Check(new User { Id = 913, RenewalDiscountSelectionJson = user.RenewalDiscountSelectionJson }, tenant, 200000).Failure);
    }
    /// <summary>Only an unpayable two-hour renewal or durable paid receipt changes capacity; ambiguous gateway attempts remain reserved.</summary>
    [Fact]
    public async Task TenantDiscount_Reconciliation_releases_unpayable_renewal_consumes_paid_receipt_and_holds_uncertain_invoice()
    {
        using var databases = new Databases();
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        var tenant = new BotInstance { Id = "tenant-reconciliation", Username = "reconciliation",
            Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 711, TenantStoreNumber = 1, Enabled = true };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var discounts = provider.GetRequiredService<TenantDiscountService>();
        var definition = await discounts.SaveCodeAsync(tenant.Id, 711,
            new TenantDiscountCodeInput("settle-30", TenantDiscountKinds.Fixed, TenantDiscountScopes.Renew,
                30000, null, null, 0, 1, true));
        Assert.True(definition.Success);
        var selected = new TenantDiscountSelection(definition.Value.Id, definition.Value.UpdatedAtUtc,
            new TenantDiscountPrice(200000, 150000, 30000, 170000));
        TenantBotOrder Renewal(long customerId, DateTime created) => new()
        {
            OrderId = Guid.NewGuid().ToString("N"), TenantBotId = tenant.Id, TenantBotUsername = tenant.Username,
            OwnerTelegramUserId = 711, CustomerTelegramUserId = customerId, CustomerChatId = customerId,
            OrderKind = TenantBotOrderKinds.Renew, ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1",
            AccountCount = 1, TargetAccountEmail = $"customer-{customerId}", TargetAccountUuid = Guid.NewGuid().ToString(),
            SalePriceToman = 200000, BaseCostToman = 150000, ProfitToman = 50000,
            PaymentProvider = "pending", PaymentStatus = TenantBotOrderStatuses.Pending, CreatedAtUtc = created
        };
        var stale = await discounts.AdmitRenewalOrderAsync(Renewal(912, DateTime.UtcNow.AddHours(-3)), selected, 200000, 150000);
        Assert.True(stale.Success);
        int staleClaimId;
        await using (var db = databases.Users.CreateDbContext())
            staleClaimId = (await db.TenantDiscountRedemptions.AsNoTracking()
                .SingleAsync(x => x.TenantBotOrderId == stale.Value.Id)).Id;
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        Assert.True(await service.ReconcileTenantDiscountReservationAsync(staleClaimId));
        Assert.False(await service.ReconcileTenantDiscountReservationAsync(staleClaimId));
        Assert.Equal(1, (await discounts.GetUsageAsync(tenant.Id, 711, definition.Value.Id)).Value.Remaining);

        var paid = await discounts.AdmitRenewalOrderAsync(Renewal(913, DateTime.UtcNow), selected, 200000, 150000);
        Assert.True(paid.Success);
        int paidClaimId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var order = await db.TenantBotOrders.SingleAsync(x => x.Id == paid.Value.Id);
            order.PaymentProvider = "tenant_card";
            order.PaymentStatus = TenantBotOrderStatuses.ReceiptApproved;
            db.TenantManualPaymentReceipts.Add(new TenantManualPaymentReceipt
            {
                TenantBotOrderId = order.Id, OrderId = order.OrderId, TenantBotId = order.TenantBotId,
                OwnerTelegramUserId = 711, CustomerTelegramUserId = 913, CustomerChatId = 913,
                AmountToman = order.SalePriceToman, PhotoFileId = "receipt-proof",
                Status = TenantManualPaymentReceiptStatuses.Approved,
                ApprovedAtUtc = DateTime.UtcNow, FinalConfirmedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            paidClaimId = (await db.TenantDiscountRedemptions.SingleAsync(x => x.TenantBotOrderId == order.Id)).Id;
        }
        Assert.True(await service.ReconcileTenantDiscountReservationAsync(paidClaimId));
        var usage = await discounts.GetUsageAsync(tenant.Id, 711, definition.Value.Id);
        Assert.Equal(new TenantDiscountUsage(1, 0, 0), usage.Value);
        Assert.Equal(TenantDiscountFailure.Exhausted,
            (await discounts.AdmitRenewalOrderAsync(Renewal(914, DateTime.UtcNow), selected, 200000, 150000)).Failure);

        var uncertainDefinition = await discounts.SaveCodeAsync(tenant.Id, 711,
            new TenantDiscountCodeInput("uncertain-30", TenantDiscountKinds.Fixed, TenantDiscountScopes.Renew,
                30000, null, null, 0, 1, true));
        Assert.True(uncertainDefinition.Success);
        var uncertainSelection = selected with { CodeId = uncertainDefinition.Value.Id,
            CodeUpdatedAtUtc = uncertainDefinition.Value.UpdatedAtUtc };
        var uncertain = await discounts.AdmitRenewalOrderAsync(Renewal(915, DateTime.UtcNow.AddHours(-3)),
            uncertainSelection, 200000, 150000);
        Assert.True(uncertain.Success);
        int uncertainClaimId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var order = await db.TenantBotOrders.SingleAsync(x => x.Id == uncertain.Value.Id);
            order.PaymentProvider = "hooshpay";
            order.DiscountInvoiceAttemptState = "started";
            order.DiscountInvoiceAttemptedAtUtc = DateTime.UtcNow.AddHours(-3);
            await db.SaveChangesAsync();
            uncertainClaimId = (await db.TenantDiscountRedemptions.SingleAsync(x => x.TenantBotOrderId == order.Id)).Id;
        }
        Assert.False(await service.ReconcileTenantDiscountReservationAsync(uncertainClaimId));
        Assert.Equal(1, (await discounts.GetUsageAsync(tenant.Id, 711, uncertainDefinition.Value.Id)).Value.Reserved);
        var cardDefinition = await discounts.SaveCodeAsync(tenant.Id, 711,
            new TenantDiscountCodeInput("card-expire", TenantDiscountKinds.Fixed, TenantDiscountScopes.Renew,
                30000, null, null, 0, 1, true));
        Assert.True(cardDefinition.Success);
        var cardSelection = selected with { CodeId = cardDefinition.Value.Id,
            CodeUpdatedAtUtc = cardDefinition.Value.UpdatedAtUtc };
        var card = await discounts.AdmitRenewalOrderAsync(Renewal(916, DateTime.UtcNow.AddHours(-3)),
            cardSelection, 200000, 150000);
        Assert.True(card.Success);
        int cardClaimId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var order = await db.TenantBotOrders.SingleAsync(x => x.Id == card.Value.Id);
            order.PaymentProvider = "tenant_card";
            order.PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt;
            order.DiscountInvoiceAttemptState = "created";
            await db.SaveChangesAsync();
            cardClaimId = (await db.TenantDiscountRedemptions.SingleAsync(x => x.TenantBotOrderId == order.Id)).Id;
        }
        Assert.True(await service.ReconcileTenantDiscountReservationAsync(cardClaimId));
        await using var expired = databases.Users.CreateDbContext();
        Assert.Equal(TenantBotOrderStatuses.DiscountExpired,
            (await expired.TenantBotOrders.AsNoTracking().SingleAsync(x => x.Id == card.Value.Id)).PaymentStatus);
        Assert.Equal(1, (await discounts.GetUsageAsync(tenant.Id, 711, cardDefinition.Value.Id)).Value.Remaining);
    }
    /// <summary>The real renewal confirmation admits only the displayed net and a disabled code never becomes a gross order.</summary>
    [Fact]
    public async Task TenantDiscount_Renewal_confirmation_reserves_once_and_fails_closed_after_code_disable()
    {
        using var databases = new Databases();
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        var tenant = new BotInstance { Id = "tenant-renew-confirm", Username = "renew_confirm",
            Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 711, TenantStoreNumber = 1, Enabled = true,
            TenantCardPaymentEnabled = true, TenantCardNumber = "6037991234567890",
            TenantCardHolderName = "Test" };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var discounts = provider.GetRequiredService<TenantDiscountService>();
        var code = await discounts.SaveCodeAsync(tenant.Id, 711,
            new TenantDiscountCodeInput("renew-30", TenantDiscountKinds.Fixed, TenantDiscountScopes.Renew,
                30000, null, null, 0, 2, true));
        Assert.True(code.Success);
        var selection = new XuiV3PurchaseSelection
        {
            ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1
        };
        var key = (string)typeof(TenantBotService).GetMethod("BUILDPAYACTION",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { selection })!;
        var client = new StorefrontClient();
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client
        });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var admit = typeof(TenantBotService).GetMethod("AdmitDiscountedRenewalFromStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<bool> Confirm(long id)
        {
            var user = new User { Id = id, SelectedCountry = "normal", TotoalGB = "50", SelectedPeriod = "m1",
                RenewalDiscountSelectionJson = JsonConvert.SerializeObject(new
            {
                TenantBotId = tenant.Id, CustomerTelegramUserId = id, CodeId = code.Value.Id,
                CodeUpdatedAtUtc = code.Value.UpdatedAtUtc, SelectionKey = key,
                DisplayedGrossToman = 200000, DisplayedBaseCostToman = 150000,
                DisplayedDiscountToman = 30000, DisplayedNetToman = 170000
            }) };
            var order = new TenantBotOrder
            {
                OrderId = Guid.NewGuid().ToString("N"), TenantBotId = tenant.Id,
                OwnerTelegramUserId = 711, CustomerTelegramUserId = id, CustomerChatId = id,
                OrderKind = TenantBotOrderKinds.Renew, ServiceKey = "normal",
                TrafficGb = 50, DurationKey = "m1", AccountCount = 1,
                TargetAccountEmail = $"renew-{id}", TargetAccountUuid = Guid.NewGuid().ToString(),
                PaymentProvider = "pending", SalePriceToman = 200000, BaseCostToman = 150000
            };
            return await (Task<bool>)admit.Invoke(service, new object[]
            {
                client, new ChatId(id), tenant, new CredUser { TelegramUserId = id },
                user, order, 200000L, 150000L, CancellationToken.None
            })!;
        }
        Assert.True(await Confirm(912));
        await using (var db = databases.Users.CreateDbContext())
        {
            var order = await db.TenantBotOrders.AsNoTracking().SingleAsync();
            Assert.Equal(170000, order.SalePriceToman);
            Assert.Equal(200000, order.OriginalSalePriceToman);
            Assert.Equal(30000, order.DiscountAmountToman);
            Assert.Equal("pending", order.PaymentProvider);
            Assert.Equal(TenantDiscountRedemptionStates.Reserved,
                (await db.TenantDiscountRedemptions.AsNoTracking().SingleAsync()).State);
        }
        Assert.True((await discounts.SetActiveAsync(tenant.Id, 711, code.Value.Id, false)).Success);
        Assert.True(await Confirm(913));
        await using var check = databases.Users.CreateDbContext();
        Assert.Equal(1, await check.TenantBotOrders.CountAsync());
        Assert.Equal(1, await check.TenantDiscountRedemptions.CountAsync());
        Assert.Contains("غیرفعال", client.Texts.Last());
    }
    /// <summary>Wallet quote admission, reservation and immutable net debit share one checkout; insufficient funds never hold capacity.</summary>
    [Fact]
    public async Task TenantDiscount_Quoted_wallet_debits_net_once_and_definitive_insufficiency_releases_claim()
    {
        using var databases = new Databases();
        var (wallet, funding, _) = await SeedCustomerWalletOrderAsync(databases);
        var discounts = new TenantDiscountService(databases.Users, new ServiceSalesAvailabilityService(new AppConfig(),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tenant-discount-wallet-fixture-policy.json"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceSalesAvailabilityService>.Instance));
        var definition = await discounts.SaveCodeAsync("tenant-a", 456,
            new TenantDiscountCodeInput("wallet-30", TenantDiscountKinds.Fixed, TenantDiscountScopes.Purchase,
                30000, null, null, 0, 2, true));
        Assert.True(definition.Success);
        async Task<(TenantDiscountQuote Quote, TenantBotOrder Order)> Display(int messageId, long gross, long cost)
        {
            var quote = await discounts.CreateQuoteAsync("tenant-a", 123, 123, "normal:50:m1:1", gross, cost);
            Assert.True((await discounts.BindQuoteMessageAsync(quote.Id, "tenant-a", 123, 123, messageId)).Success);
            var price = TenantDiscountService.Quote(definition.Value, gross, cost, TenantDiscountScopes.Purchase);
            Assert.True(price.Success);
            Assert.True((await discounts.SelectQuoteCodeAsync(quote.Id, "tenant-a", 123, 123,
                messageId, quote.SelectionKey,
                new TenantDiscountSelection(definition.Value.Id, definition.Value.UpdatedAtUtc, price.Value),
                gross, cost)).Success);
            return (quote, new TenantBotOrder
            {
                OrderId = Guid.NewGuid().ToString("N"), TenantBotId = "tenant-a", OwnerTelegramUserId = 456,
                CustomerTelegramUserId = 123, CustomerChatId = 123, OrderKind = TenantBotOrderKinds.Purchase,
                ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1,
                SalePriceToman = gross, BaseCostToman = cost, PaymentProvider = "wallet"
            });
        }
        var first = await Display(42, 200000, 150000);
        var key = "tcw-quote:tenant-a:123:123:42";
        var admitted = await funding.AdmitQuotedAsync(first.Order, key, first.Quote.Id, 42,
            first.Quote.SelectionKey, 200000, 150000);
        Assert.Equal(170000, admitted.SalePriceToman);
        Assert.Equal(30000, admitted.DiscountAmountToman);
        var duplicate = await funding.AdmitQuotedAsync(new TenantBotOrder
        {
            TenantBotId = "tenant-a", OwnerTelegramUserId = 456, CustomerTelegramUserId = 123,
            CustomerChatId = 123, OrderId = Guid.NewGuid().ToString("N"), OrderKind = TenantBotOrderKinds.Purchase,
            ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1,
            SalePriceToman = 200000, BaseCostToman = 150000, PaymentProvider = "wallet"
        }, key, first.Quote.Id, 42, first.Quote.SelectionKey, 200000, 150000);
        Assert.Equal(admitted.Id, duplicate.Id);
        Assert.True(await funding.DebitAsync(admitted, freshGrossToman: 200000, freshBaseCostToman: 150000));
        Assert.True(await funding.DebitAsync(duplicate, freshGrossToman: 200000, freshBaseCostToman: 150000));
        Assert.Equal(330000, await wallet.GetAccountBalance(123));
        Assert.Equal(-170000, (await wallet.GetWalletOperationAsync(TenantCustomerWalletFunding.DebitKey(admitted.Id)))!.AmountToman);

        var second = await Display(43, 500000, 400000);
        var insufficient = await funding.AdmitQuotedAsync(second.Order, "tcw-quote:tenant-a:123:123:43",
            second.Quote.Id, 43, second.Quote.SelectionKey, 500000, 400000);
        Assert.False(await funding.DebitAsync(insufficient, freshGrossToman: 500000, freshBaseCostToman: 400000));
        Assert.Equal(330000, await wallet.GetAccountBalance(123));
        Assert.Null(await wallet.GetWalletOperationAsync(TenantCustomerWalletFunding.DebitKey(insufficient.Id)));
        await using var db = databases.Users.CreateDbContext();
        var failed = await db.TenantBotOrders.AsNoTracking().SingleAsync(x => x.Id == insufficient.Id);
        Assert.Equal("definitive_failed", failed.CustomerWalletState);
        Assert.Equal(TenantDiscountRedemptionStates.Released,
            (await db.TenantDiscountRedemptions.AsNoTracking().SingleAsync(x => x.TenantBotOrderId == insufficient.Id)).State);
        Assert.Equal(1, (await discounts.GetUsageAsync("tenant-a", 456, definition.Value.Id)).Value.Reserved);
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        await using var scope = provider.CreateAsyncScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var claimId = (await db.TenantDiscountRedemptions.AsNoTracking()
            .SingleAsync(x => x.TenantBotOrderId == admitted.Id)).Id;
        Assert.True(await reconciler.ReconcileTenantDiscountReservationAsync(claimId));
        Assert.Equal(1, (await discounts.GetUsageAsync("tenant-a", 456, definition.Value.Id)).Value.Consumed);
        // The proven non-applied financial failure is terminal before a refund; the immutable credit then frees capacity.
        await db.TenantBotOrders.Where(x => x.Id == admitted.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentStatus, TenantBotOrderStatuses.Failed));
        Assert.True(await funding.RefundRejectedAsync(admitted.Id));
        Assert.Equal(500000, await wallet.GetAccountBalance(123));
        Assert.True(await reconciler.ReconcileTenantDiscountReservationAsync(claimId));
        Assert.Equal(TenantDiscountRedemptionStates.Released,
            (await db.TenantDiscountRedemptions.AsNoTracking().SingleAsync(x => x.Id == claimId)).State);
        Assert.Equal(2, (await discounts.GetUsageAsync("tenant-a", 456, definition.Value.Id)).Value.Remaining);
    }
}
