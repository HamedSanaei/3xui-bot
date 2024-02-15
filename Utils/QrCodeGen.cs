using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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


        public static byte[] GenerateQRCodeWithMargin(string text, int marginSize, string title)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            Bitmap qrCodeImage = new QRCode(qrCodeData).GetGraphic(20);

            // Calculate the new dimensions with added margins
            int newWidth = qrCodeImage.Width + 2 * marginSize;
            int newHeight = qrCodeImage.Height + 2 * marginSize + 50; // Adjust 50 to your needed title space

            // Create a larger blank canvas
            Bitmap canvas = new Bitmap(newWidth, newHeight + 50); // Add 50px or more for the title

            using (Graphics g = Graphics.FromImage(canvas))
            {
                // Set the background color (optional)
                g.Clear(Color.White);

                // Draw the title above the QR code
                using (Font font = new Font("Arial", 20)) // Choose your font and size
                {
                    SizeF titleSize = g.MeasureString(title, font);
                    PointF titlePosition = new PointF((newWidth - titleSize.Width) / 2, marginSize / 2);
                    g.DrawString(title, font, Brushes.Black, titlePosition);
                }

                // Draw the QR code in the center with added margins below the title
                g.DrawImage(qrCodeImage, marginSize, marginSize + 50, qrCodeImage.Width, qrCodeImage.Height); // Adjust the Y position
            }

            using (MemoryStream stream = new MemoryStream())
            {
                canvas.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }



        public static byte[] GenerateQRCodeWithStyledHeader(string text, int marginSize, string title)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            Bitmap qrCodeImage = new QRCode(qrCodeData).GetGraphic(20);

            // Define the size and style of the header
            int headerHeight = 100; // Example height for the header
            int cornerRadius = 30; // Radius for the rounded corners
            Font titleFont = new Font("Arial", 60); // Example font for the title

            // Calculate the new dimensions with added margins and header
            int newWidth = qrCodeImage.Width + 2 * marginSize;
            int newHeight = qrCodeImage.Height + 2 * marginSize + headerHeight / 2;

            // Create a larger blank canvas with space for the header
            Bitmap canvas = new Bitmap(newWidth, newHeight);

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White); // Set the background color of the entire image

                // Draw the rounded rectangle for the header
                using (GraphicsPath path = new GraphicsPath())
                {
                    // Create a path with rounded corners
                    path.AddArc(0, 0, cornerRadius * 2, cornerRadius * 2, 180, 90);
                    path.AddArc(newWidth - cornerRadius * 2, 0, cornerRadius * 2, cornerRadius * 2, 270, 90);
                    path.AddArc(newWidth - cornerRadius * 2, headerHeight - cornerRadius * 2, cornerRadius * 2, cornerRadius * 2, 0, 90);
                    path.AddArc(0, headerHeight - cornerRadius * 2, cornerRadius * 2, cornerRadius * 2, 90, 90);
                    path.CloseFigure();

                    // Fill the path with a color or gradient
                    g.FillPath(new SolidBrush(Color.GreenYellow), path); // Use a SolidBrush or LinearGradientBrush for gradients
                }

                // Draw the header text
                SizeF titleSize = g.MeasureString(title, titleFont);
                PointF titlePosition = new PointF((newWidth - titleSize.Width) / 2, (headerHeight - titleSize.Height) / 2);
                g.DrawString(title, titleFont, Brushes.ForestGreen, titlePosition);

                // Draw the QR code below the header
                g.DrawImage(qrCodeImage, marginSize, headerHeight + marginSize, qrCodeImage.Width, qrCodeImage.Height);
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