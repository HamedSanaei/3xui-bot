using System;
using System.Drawing;
using System.Drawing.Imaging;
using QRCoder;



namespace Adminbot.Utils
{
    public class QrCodeGen
    {

        public static byte[] GenerateQRCodeWithMargin(string text, int marginSize)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            Bitmap qrCodeImage = new QRCode(qrCodeData).GetGraphic(20);

            // Calculate the new dimensions with added margins
            int newWidth = qrCodeImage.Width + 2 * marginSize;
            int newHeight = qrCodeImage.Height + 2 * marginSize;

            // Create a larger blank canvas
            Bitmap canvas = new Bitmap(newWidth, newHeight);

            using (Graphics g = Graphics.FromImage(canvas))
            {
                // Set the background color (optional)
                g.Clear(Color.White);

                // Draw the QR code in the center with added margins
                g.DrawImage(qrCodeImage, marginSize, marginSize, qrCodeImage.Width, qrCodeImage.Height);
            }
            using (MemoryStream stream = new MemoryStream())
            {
                canvas.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }

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