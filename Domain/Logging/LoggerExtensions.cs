using Microsoft.Extensions.Logging;
namespace Adminbot.Domain.Logging
{

    public static class LoggerExtensions
    {
        public static void LogPayment(this ILogger logger, string message)
        {
            logger.Log(LogLevel.Information, new EventId(1000, "Payment"), message, null, (msg, ex) => msg);
        }
    }

}