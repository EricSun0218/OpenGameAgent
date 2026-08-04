using System.Net;
using System.Net.Sockets;
using System.Text;
using GameAgent.Providers.Native;
using Xunit;

namespace GameAgent.Providers.Native.Tests;

public sealed class NativeProviderHttpTransportTests
{
    [Fact]
    public async Task SendsBearerCredentialWithoutTreatingSchemeAsTokenWhitespace()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var received = ReceiveOnceAsync(listener);
        using var transport = new HttpClientNativeProviderTransport();

        using var response = await transport.SendAsync(
            new NativeProviderHttpRequest
            {
                Uri = new Uri(
                    $"http://127.0.0.1:{endpoint.Port}/responses"),
                CredentialHeaderName = "Authorization",
                CredentialHeaderValue = "Bearer test-token",
                Body = Encoding.UTF8.GetBytes("{}")
            },
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(
            "Bearer test-token",
            await received.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<string?> ReceiveOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4_096,
            leaveOpen: true);
        string? authorization = null;
        var contentLength = 0;
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            if (line.StartsWith(
                    "Authorization:",
                    StringComparison.OrdinalIgnoreCase))
            {
                authorization = line.Substring("Authorization:".Length)
                    .Trim();
            }
            else if (line.StartsWith(
                         "Content-Length:",
                         StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(
                    line.Substring("Content-Length:".Length).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var body = new char[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var count = await reader.ReadAsync(
                body.AsMemory(offset, body.Length - offset));
            if (count == 0)
            {
                break;
            }

            offset += count;
        }

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(response);
        await stream.FlushAsync();
        return authorization;
    }
}
