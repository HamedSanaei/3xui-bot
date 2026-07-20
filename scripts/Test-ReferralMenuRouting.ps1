$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$servicePath = Join-Path $repositoryRoot 'Services\TelegramBotService.cs'
$extensionsPath = Join-Path $repositoryRoot 'Utils\TelegramBotClientExtensions.cs'
$source = [System.IO.File]::ReadAllText($servicePath, [System.Text.Encoding]::UTF8)
$extensionsSource = [System.IO.File]::ReadAllText($extensionsPath, [System.Text.Encoding]::UTF8)

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Scenario
    )

    if (-not $Condition) {
        throw "Referral routing regression failed: $Scenario"
    }

    Write-Host "PASS: $Scenario"
}

function Assert-Ordered {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Earlier,

        [Parameter(Mandatory = $true)]
        [string]$Later,

        [Parameter(Mandatory = $true)]
        [string]$Scenario
    )

    $earlierIndex = $Text.IndexOf($Earlier, [System.StringComparison]::Ordinal)
    $laterIndex = $Text.IndexOf($Later, [System.StringComparison]::Ordinal)
    Assert-True ($earlierIndex -ge 0 -and $laterIndex -gt $earlierIndex) $Scenario
}

$coreStart = $source.IndexOf('private async Task HandleUpdateCoreAsync(', [System.StringComparison]::Ordinal)
$regularStart = $source.IndexOf('private async Task HandleUpdateRegularUsers(', [System.StringComparison]::Ordinal)
$helperStart = $source.IndexOf('private async Task<bool> TryHandleReferralMenuCommandAsync(', [System.StringComparison]::Ordinal)
$dashboardStart = $source.IndexOf('private async Task<Message> SendReferralDashboardAsync(', [System.StringComparison]::Ordinal)
$supportHelperStart = $source.IndexOf('private static string BuildOwnedBotSupportContactHtml(', [System.StringComparison]::Ordinal)

Assert-True ($coreStart -ge 0 -and $regularStart -gt $coreStart) 'owned message routing methods are present'
Assert-True ($helperStart -gt $regularStart -and $dashboardStart -gt $helperStart -and $supportHelperStart -gt $dashboardStart) 'central referral handler and dashboard are present'

$core = $source.Substring($coreStart, $regularStart - $coreStart)
$regular = $source.Substring($regularStart, $helperStart - $regularStart)
$helper = $source.Substring($helperStart, $dashboardStart - $helperStart)
$dashboard = $source.Substring($dashboardStart, $supportHelperStart - $dashboardStart)
$strictSenderStart = $extensionsSource.IndexOf('public static async Task<Message> SendReferralTextMessageAsync(', [System.StringComparison]::Ordinal)
$nextExtensionStart = $extensionsSource.IndexOf('public static async Task CustomForwardMessage(', [System.StringComparison]::Ordinal)
Assert-True ($strictSenderStart -ge 0 -and $nextExtensionStart -gt $strictSenderStart) 'strict referral Telegram sender is present'
$strictSender = $extensionsSource.Substring($strictSenderStart, $nextExtensionStart - $strictSenderStart)

# Command recognition must be normalized and support both visible reply-keyboard forms.
$plainCommand = -join @(
    0x062F, 0x0639, 0x0648, 0x062A, 0x0020,
    0x0627, 0x0632, 0x0020,
    0x062F, 0x0648, 0x0633, 0x062A, 0x0627, 0x0646
).ForEach({ [char]$_ })
$emojiCommand = [char]::ConvertFromUtf32(0x1F381) + ' ' + $plainCommand
Assert-True ($source.Contains('var normalized = text?.Trim();')) 'referral command trims surrounding whitespace'
Assert-True ($source.Contains('"' + $emojiCommand + '"')) 'emoji referral command is recognized'
Assert-True ($source.Contains('"' + $plainCommand + '"')) 'plain referral command is recognized'

# Tenant routing and payment-return starts remain ahead of the super-admin shortcut.
Assert-Ordered $core 'BotInstanceTypes.Tenant' 'if (isSuperAdmin && IsReferralMenuCommand(message.Text))' 'tenant bots are excluded before referral routing'
Assert-Ordered $core 'TryHandleNowPaymentsReturnStartAsync(' 'if (isSuperAdmin && IsReferralMenuCommand(message.Text))' 'payment return payloads retain priority'
Assert-Ordered $core 'if (isSuperAdmin && IsReferralMenuCommand(message.Text))' 'if (!isSuperAdmin)' 'super-admin referral route bypasses the legacy admin chain'

# Referral registration stays ahead of the dashboard command, which stays ahead of arbitrary-text flow handlers.
Assert-Ordered $regular 'ReferralService.TryParseStartPayload(' 'TryHandleReferralMenuCommandAsync(' '/start referral registration retains priority'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleOwnerMessageAsync(' 'referral command precedes tenant-owner state text'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleAccountActionAsync(' 'referral command precedes account actions'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleDeleteExpiredAccountsAsync(' 'referral command precedes expired-account deletion state'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleAccountCommentTextAsync(' 'referral command precedes account-comment state'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleAccountSearchAsync(' 'referral command precedes account-search state'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleColleagueRequestAsync(' 'referral command precedes colleague-request state'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleFreeTrialAsync(' 'referral command precedes trial state'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandlePurchaseTextAsync(' 'referral command precedes purchase text state'
Assert-Ordered $regular 'TryHandleReferralMenuCommandAsync(' 'TryHandleRenewAsync(' 'referral command precedes renewal text state'

# The central handler cancels only transient bot-scoped state/session data before rendering.
Assert-Ordered $helper '_userDbContext.ClearUserStatus(' '_xuiV3PurchaseSessionStore.Clear(' 'persisted flow is cleared before the purchase session'
Assert-Ordered $helper '_xuiV3PurchaseSessionStore.Clear(' 'SendReferralDashboardAsync(' 'all transient flow state is cleared before dashboard delivery'
Assert-True ($helper.Contains('BotInstanceTypes.Owned')) 'central handler rejects non-owned bot contexts'
Assert-True ($helper.Contains('Owned-bot referral menu routed.')) 'successful routes emit structured context logs'
Assert-True ($helper.Contains('Owned-bot referral menu failed.')) 'routing and statistics failures emit structured exception logs'
Assert-True ($helper.Contains('Referral dashboard failure notification could not be delivered.')) 'routing failures attempt a safe user-facing notification'
Assert-True ($helper.Contains('var dashboardSent = dashboardMessage != null;')) 'dashboard success is derived from the returned Telegram message'
Assert-True ($helper.Contains('telegramApiException?.ErrorCode')) 'Telegram API error codes are included in structured failure logs'

# Existing dashboard behavior must remain explicit for disabled configuration and missing bot usernames.
Assert-True ($dashboard.Contains('_appConfig.Referral?.Enabled != true')) 'disabled referral configuration remains explicit'
Assert-True ($dashboard.Contains('string.IsNullOrWhiteSpace(username)')) 'missing owned-bot username remains explicit'
Assert-True ($dashboard.Contains('return await botClient.SendReferralTextMessageAsync(')) 'all referral dashboard outcomes return the accepted Telegram message'
Assert-True (-not $dashboard.Contains('CustomSendTextMessageAsync(')) 'referral dashboard bypasses the exception-swallowing sender'
Assert-True ($strictSender.Contains('parseMode: null')) 'referral sender disables Telegram entity parsing'
Assert-True (-not $strictSender.Contains('catch (')) 'strict referral sender propagates Telegram failures'
Assert-True ($strictSender.Contains('result ?? throw new InvalidOperationException(')) 'strict referral sender rejects a null Telegram message'
Assert-True ($regular.Contains('RegisterRelationshipAsync(')) '/start ref registration remains connected to the existing referral service'

Write-Host 'Referral menu routing regression checks completed successfully.'
