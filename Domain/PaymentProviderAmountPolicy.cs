using System.Globalization;
using Adminbot.Utils;

namespace Adminbot.Domain;

/// <summary>
/// Defines the provider-enforced invoice amount range for HooshPay in Iranian toman.
/// </summary>
/// <remarks>
/// The limits come from HooshPay's production validation response. They are provider contract limits, not wallet
/// pricing rules, and therefore apply equally to owned-wallet charges and tenant purchase or renewal invoices.
/// </remarks>
public static class HooshPayAmountPolicy
{
    /// <summary>Smallest HooshPay invoice amount accepted by the provider, in Iranian toman.</summary>
    public const long MinimumAmountToman = 50_000;

    /// <summary>Largest HooshPay invoice amount accepted by the provider, in Iranian toman.</summary>
    public const long MaximumAmountToman = 1_000_000;

    /// <summary>
    /// Determines whether a toman amount is inside HooshPay's inclusive invoice range.
    /// </summary>
    /// <param name="amountToman">
    /// Provider invoice amount in Iranian toman before the buyer-paid gateway fee. Both configured boundaries are
    /// accepted; values outside them are rejected before any HTTP request or local payment row is created.
    /// </param>
    /// <returns>
    /// <c>true</c> when <paramref name="amountToman"/> is between 50,000 and 1,000,000 toman inclusive; otherwise
    /// <c>false</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// HooshPayAmountPolicy.IsValid(50_000);   // true
    /// HooshPayAmountPolicy.IsValid(1_000_001); // false
    /// </code>
    /// </example>
    public static bool IsValid(long amountToman)
        => amountToman >= MinimumAmountToman && amountToman <= MaximumAmountToman;

    /// <summary>
    /// Rejects an amount that HooshPay cannot accept before invoice creation reaches the network.
    /// </summary>
    /// <param name="amountToman">Base invoice amount in Iranian toman, excluding the gateway fee.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the amount is below 50,000 or above 1,000,000 toman.
    /// </exception>
    /// <remarks>
    /// Provider clients must call this method before building or sending a request. Telegram flows should call
    /// <see cref="IsValid(long)"/> first so they can show <see cref="BuildUserMessage"/> without using exceptions as
    /// normal user-input control flow.
    /// </remarks>
    public static void EnsureValid(long amountToman)
    {
        if (!IsValid(amountToman))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amountToman),
                amountToman,
                $"HooshPay invoice amount must be between {MinimumAmountToman.ToString(CultureInfo.InvariantCulture)} and {MaximumAmountToman.ToString(CultureInfo.InvariantCulture)} toman inclusive.");
        }
    }

    /// <summary>
    /// Builds the customer-facing Persian validation message for an unsupported HooshPay amount.
    /// </summary>
    /// <returns>A safe message containing only the public provider boundaries and no credentials or endpoint data.</returns>
    /// <remarks>Use after <see cref="IsValid(long)"/> returns false in owned or tenant Telegram flows.</remarks>
    /// <example>
    /// <code>
    /// if (!HooshPayAmountPolicy.IsValid(amountToman))
    ///     await bot.SendTextMessageAsync(chatId, HooshPayAmountPolicy.BuildUserMessage());
    /// </code>
    /// </example>
    public static string BuildUserMessage()
        => $"مبلغ پرداخت هوش‌پی باید بین {MinimumAmountToman.FormatCurrency()} تا {MaximumAmountToman.FormatCurrency()} باشد.";
}

/// <summary>
/// Defines the provider-enforced lower boundary for UniquePay invoices in Iranian toman.
/// </summary>
/// <remarks>
/// Production behavior rejects exactly 50,000 toman and accepts amounts above it. UniquePay has not exposed a
/// corresponding maximum in the current integration, so this policy deliberately applies only an exclusive minimum.
/// </remarks>
public static class UniquePayAmountPolicy
{
    /// <summary>Exclusive UniquePay invoice lower boundary in Iranian toman.</summary>
    public const long ExclusiveMinimumAmountToman = 50_000;

    /// <summary>
    /// Determines whether a toman amount is above UniquePay's exclusive minimum.
    /// </summary>
    /// <param name="amountToman">
    /// Base UniquePay amount in Iranian toman before the configured provider fee. Exactly 50,000 is invalid;
    /// 50,001 and larger values are valid.
    /// </param>
    /// <returns><c>true</c> only when <paramref name="amountToman"/> is greater than 50,000 toman.</returns>
    /// <example>
    /// <code>
    /// UniquePayAmountPolicy.IsValid(50_000); // false
    /// UniquePayAmountPolicy.IsValid(50_001); // true
    /// </code>
    /// </example>
    public static bool IsValid(long amountToman)
        => amountToman > ExclusiveMinimumAmountToman;

    /// <summary>
    /// Rejects an amount that UniquePay cannot accept before invoice creation reaches the network.
    /// </summary>
    /// <param name="amountToman">Base invoice amount in Iranian toman, excluding the provider fee.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the amount is less than or equal to 50,000 toman.
    /// </exception>
    /// <remarks>
    /// This boundary is enforced by the provider client in addition to owned and tenant UI guards, so crafted or
    /// stale Telegram callbacks cannot cause an invalid external request.
    /// </remarks>
    public static void EnsureValid(long amountToman)
    {
        if (!IsValid(amountToman))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amountToman),
                amountToman,
                $"UniquePay invoice amount must be greater than {ExclusiveMinimumAmountToman.ToString(CultureInfo.InvariantCulture)} toman.");
        }
    }

    /// <summary>
    /// Builds the customer-facing Persian validation message for an unsupported UniquePay amount.
    /// </summary>
    /// <returns>A safe message containing the exclusive minimum and no provider credentials or internal details.</returns>
    /// <remarks>Use after <see cref="IsValid(long)"/> returns false in owned or tenant Telegram flows.</remarks>
    /// <example>
    /// <code>
    /// if (!UniquePayAmountPolicy.IsValid(amountToman))
    ///     await bot.SendTextMessageAsync(chatId, UniquePayAmountPolicy.BuildUserMessage());
    /// </code>
    /// </example>
    public static string BuildUserMessage()
        => $"مبلغ پرداخت یونیک‌پی باید بیشتر از {ExclusiveMinimumAmountToman.FormatCurrency()} باشد.";
}
