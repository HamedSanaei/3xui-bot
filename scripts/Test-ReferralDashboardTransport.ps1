$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $tempBase ('adminbot-referral-transport-' + [Guid]::NewGuid().ToString('N'))))

if (-not $tempRoot.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The generated transport-test directory is outside the operating-system temp directory.'
}

[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null

try {
    $buildOutput = Join-Path $repositoryRoot 'bin\Debug\net10.0'
    $adminbotAssembly = Join-Path $buildOutput 'Adminbot.dll'
    $telegramAssembly = Join-Path $buildOutput 'Telegram.Bot.dll'
    if (-not (Test-Path -LiteralPath $adminbotAssembly) -or
        -not (Test-Path -LiteralPath $telegramAssembly)) {
        throw 'Build Adminbot.sln before running the referral transport test.'
    }

    $escapedAdminbotAssembly = [System.Security.SecurityElement]::Escape($adminbotAssembly)
    $escapedTelegramAssembly = [System.Security.SecurityElement]::Escape($telegramAssembly)
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Adminbot">
      <HintPath>$escapedAdminbotAssembly</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="Telegram.Bot">
      <HintPath>$escapedTelegramAssembly</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
</Project>
"@

    $program = @'
using System.Net;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

const string referralLink = "https://t.me/example_bot?start=ref_abc123";
const string successResponse = "{\"ok\":true,\"result\":{\"message_id\":42,\"date\":1710000000,\"chat\":{\"id\":123456,\"type\":\"private\"},\"text\":\"Referral link\"}}";

var successHandler = new CaptureHandler(HttpStatusCode.OK, successResponse);
var successClient = new TelegramBotClient(
    "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
    new HttpClient(successHandler));

Message sent = await successClient.SendReferralTextMessageAsync(
    chatId: new ChatId(123456),
    text: referralLink,
    cancellationToken: CancellationToken.None);

Assert(sent is not null && sent.MessageId == 42, "Telegram success response must produce a non-null Message.");
Assert(successHandler.RequestBody?.Contains(referralLink, StringComparison.Ordinal) == true,
    "Serialized Telegram request must contain the ref_<code> link.");
Assert(successHandler.RequestBody?.Contains("parse_mode", StringComparison.OrdinalIgnoreCase) != true,
    "Plain referral delivery must omit parse_mode.");

const string failureResponse = "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: can't parse entities\"}";
var failureHandler = new CaptureHandler(HttpStatusCode.BadRequest, failureResponse);
var failureClient = new TelegramBotClient(
    "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
    new HttpClient(failureHandler));

try
{
    _ = await failureClient.SendReferralTextMessageAsync(
        chatId: new ChatId(123456),
        text: referralLink,
        cancellationToken: CancellationToken.None);
    throw new InvalidOperationException("Telegram HTTP 400 was incorrectly converted into a successful/null result.");
}
catch (ApiRequestException exception) when (exception.ErrorCode == 400)
{
    Assert(exception.Message.Contains("can't parse entities", StringComparison.Ordinal),
        "Telegram response text must remain available on the propagated exception.");
}

Console.WriteLine("Referral dashboard Telegram transport checks completed successfully.");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class CaptureHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseBody;

    public CaptureHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    public string RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
    }
}
'@

    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $projectPath = Join-Path $tempRoot 'ReferralTransportTest.csproj'
    $programPath = Join-Path $tempRoot 'Program.cs'
    [System.IO.File]::WriteAllText($projectPath, $project, $utf8)
    [System.IO.File]::WriteAllText($programPath, $program, $utf8)

    & dotnet restore $projectPath --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) {
        throw "Referral transport test restore failed with exit code $LASTEXITCODE."
    }

    & dotnet run --project $projectPath --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Referral transport test failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ((Test-Path -LiteralPath $tempRoot) -and
        $tempRoot.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
