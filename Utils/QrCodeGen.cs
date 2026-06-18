using System;
using QRCoder;

namespace Adminbot.Utils
{
    public class QrCodeGen
    {
        private const int PixelsPerModule = 20;

        public static byte[] GenerateQRCodeWithMargin(string text, int marginSize)
        {
            return GeneratePngQrCode(text);
        }

        public static byte[] GenerateQRCodeWithMargin(string text, int marginSize, string title)
        {
            return GeneratePngQrCode(text);
        }

        public static byte[] GenerateQRCodeWithStyledHeader(string text, int marginSize, string title)
        {
            return GeneratePngQrCode(text);
        }

        private static byte[] GeneratePngQrCode(string text)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(text ?? string.Empty, QRCodeGenerator.ECCLevel.M);
            var qrCode = new PngByteQRCode(qrCodeData);
            return qrCode.GetGraphic(PixelsPerModule);
        }

        static string RemoveVmessPrefix(string vmessLink)
        {
            const string vmessPrefix = "vmess://";
            if (vmessLink.StartsWith(vmessPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return vmessLink.Substring(vmessPrefix.Length);
            }
            return vmessLink;
        }
    }
}
