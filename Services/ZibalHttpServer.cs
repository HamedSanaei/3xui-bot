using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Adminbot.Services
{

    public class SimpleHttpServerService : IHostedService
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        public SimpleHttpServerService(string certificatePath, string certificatePassword)
        {
            _listener = new HttpListener();
            // Update this to your HTTPS URL
            _listener.Prefixes.Add("https://payment.feelpower.ir:5001/callback/");
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Load the certificate
            LoadCertificate();
            _listener.Start();
            Console.WriteLine("Server started listening for HTTPS requests.");

            await Task.Run(() => ProcessRequests(), cancellationToken);
        }

        private void LoadCertificate()
        {
            X509Certificate2 certificate = new X509Certificate2(_certificatePath, _certificatePassword);
            //_listener.SslConfiguration.ServerCertificate = certificate;
        }

        private void ProcessRequests()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var context = _listener.GetContext();
                    var request = context.Request;
                    if (request.Url.LocalPath == "/callback")
                    {
                        var query = request.QueryString; // This gets the query string parameters

                        var trackId = query["trackId"];
                        var success = query["success"];
                        var status = query["status"];
                        var orderId = query["orderId"];

                        Console.WriteLine($"Track ID: {trackId}, Success: {success}, Status: {status}, Order ID: {orderId}");

                        var response = context.Response;
                        string responseString = "<html><head><meta charset='utf-8'></head><body>Callback processed</body></html>";
                        var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                        response.ContentLength64 = buffer.Length;
                        var responseOutput = response.OutputStream;
                        responseOutput.Write(buffer, 0, buffer.Length);
                        responseOutput.Close();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An exception occurred: {ex.Message}");
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            _listener.Stop();
            Console.WriteLine("Server stopped listening for HTTPS requests.");
            return Task.CompletedTask;
        }
    }

}


