using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenGameAgent.Models.Auth.BuiltIn;

internal enum LoopbackStatePlacement
{
    Query,
    CallbackPath,
}

internal sealed class SecureLoopbackOAuthOptions
{
    public SecureLoopbackOAuthOptions(
        Uri authorizationEndpoint,
        string redirectHost,
        string callbackPath,
        TimeSpan loginTimeout,
        Func<Uri, string, string, Uri> buildAuthorizationUri,
        Func<string, string, string, Uri, CancellationToken, ValueTask<GameCredential>> exchangeAsync,
        int port = 0,
        LoopbackStatePlacement statePlacement = LoopbackStatePlacement.Query)
    {
        AuthorizationEndpoint = authorizationEndpoint;
        RedirectHost = redirectHost;
        CallbackPath = callbackPath;
        LoginTimeout = loginTimeout;
        BuildAuthorizationUri = buildAuthorizationUri;
        ExchangeAsync = exchangeAsync;
        Port = port;
        StatePlacement = statePlacement;
    }

    public Uri AuthorizationEndpoint { get; }

    public string RedirectHost { get; }

    public string CallbackPath { get; }

    public int Port { get; }

    public LoopbackStatePlacement StatePlacement { get; }

    public TimeSpan LoginTimeout { get; }

    public Func<Uri, string, string, Uri> BuildAuthorizationUri { get; }

    public Func<string, string, string, Uri, CancellationToken, ValueTask<GameCredential>> ExchangeAsync { get; }
}

internal static class SecureLoopbackOAuth
{
    private const int MaximumRequestBytes = 32_768;
    private const int MaximumRequestTargetBytes = 8192;
    private const int MaximumHeaders = 64;
    private const int MaximumAttempts = 16;
    private const int MaximumBindAttempts = 50;
    private const int BindRetryDelayMilliseconds = 100;

    public static async ValueTask<GameCredential> LoginAsync(
        SecureLoopbackOAuthOptions options,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        Validate(options);
        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var verifier = RandomToken(64);
        var challenge = Base64Url(Sha256(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomToken(32);
        var callbackPath = options.StatePlacement == LoopbackStatePlacement.CallbackPath
            ? options.CallbackPath.TrimEnd('/') + "/" + state
            : options.CallbackPath;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(options.LoginTimeout);
        await using var listener = await LoopbackListener.StartAsync(
            options.RedirectHost,
            options.Port,
            callbackPath,
            options.StatePlacement == LoopbackStatePlacement.Query ? state : null,
            lifetime.Token).ConfigureAwait(false);
        var redirectUri = listener.RedirectUri;
        var authorizationUri = options.BuildAuthorizationUri(redirectUri, challenge, state);
        BoundedOAuthHttp.RequireHttps(authorizationUri, nameof(options.AuthorizationEndpoint));
        if (authorizationUri.AbsoluteUri.Length > 16_384)
        {
            throw new InvalidOperationException("The OAuth authorization URL exceeded its safety bound.");
        }

        if (interaction.NotifyAsync is not null)
        {
            await interaction.NotifyAsync(
                $"Waiting for an authorization callback at {redirectUri}",
                lifetime.Token).ConfigureAwait(false);
        }

        if (interaction.OpenBrowserAsync is not null)
        {
            await interaction.OpenBrowserAsync(authorizationUri, lifetime.Token).ConfigureAwait(false);
        }
        else if (interaction.NotifyAsync is not null)
        {
            await interaction.NotifyAsync(authorizationUri.AbsoluteUri, lifetime.Token).ConfigureAwait(false);
        }

        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var callbackTask = listener.WaitForCallbackAsync(lifetime.Token);
        Task<LoopbackAuthorizationResult>? promptTask = null;
        if (interaction.PromptAsync is not null)
        {
            promptTask = ParsePromptAsync(
                interaction.PromptAsync,
                redirectUri,
                callbackPath,
                state,
                options.StatePlacement,
                promptCancellation.Token);
        }

        LoopbackAuthorizationResult result;
        try
        {
            if (promptTask is null)
            {
                result = await callbackTask.ConfigureAwait(false);
            }
            else
            {
                var completed = await Task.WhenAny(callbackTask, promptTask).ConfigureAwait(false);
                result = await completed.ConfigureAwait(false);
                if (completed == callbackTask)
                {
                    promptCancellation.Cancel();
                    Observe(promptTask);
                }
                else
                {
                    await listener.StopAsync().ConfigureAwait(false);
                    Observe(callbackTask);
                }
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The OAuth login timed out.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Error is not null)
        {
            throw new InvalidOperationException($"OAuth authorization was denied ({result.Error}).");
        }

        try
        {
            var credential = await options.ExchangeAsync(
                result.Code!,
                verifier,
                state,
                redirectUri,
                lifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return credential ?? throw new InvalidOperationException("The OAuth exchange returned no credential.");
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The OAuth login timed out.", exception);
        }
    }

    private static async Task<LoopbackAuthorizationResult> ParsePromptAsync(
        Func<string, bool, CancellationToken, ValueTask<string>> prompt,
        Uri redirectUri,
        string callbackPath,
        string state,
        LoopbackStatePlacement placement,
        CancellationToken cancellationToken)
    {
        var input = await prompt(
            "Paste the complete OAuth callback URL.",
            false,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(input)
            || input.Length > 16_384
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var callback)
            || callback.Scheme != Uri.UriSchemeHttp
            || !string.Equals(callback.Host, redirectUri.Host, StringComparison.OrdinalIgnoreCase)
            || callback.Port != redirectUri.Port
            || !FixedTimeEquals(callback.AbsolutePath, callbackPath))
        {
            throw new InvalidOperationException("The OAuth callback URL did not match the active loopback listener.");
        }

        var query = ParseQuery(callback.Query);
        if (placement == LoopbackStatePlacement.Query
            && (!query.TryGetValue("state", out var returnedState) || !FixedTimeEquals(returnedState, state)))
        {
            throw new InvalidOperationException("The OAuth callback state did not match the active login.");
        }

        if (query.TryGetValue("error", out var error))
        {
            return new LoopbackAuthorizationResult(null, Bound(error, 4096));
        }

        if (!query.TryGetValue("code", out var code) || !IsBoundedValue(code, 65_536))
        {
            throw new InvalidOperationException("The OAuth callback omitted its authorization code.");
        }

        return new LoopbackAuthorizationResult(code, null);
    }

    private static void Validate(SecureLoopbackOAuthOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        BoundedOAuthHttp.RequireHttps(options.AuthorizationEndpoint, nameof(options.AuthorizationEndpoint));
        if (options.RedirectHost is not ("127.0.0.1" or "localhost"))
        {
            throw new ArgumentException("Only an explicit IPv4 loopback host is supported.", nameof(options.RedirectHost));
        }

        if (options.Port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port));
        }

        if (string.IsNullOrWhiteSpace(options.CallbackPath)
            || options.CallbackPath.Length > 1024
            || options.CallbackPath[0] != '/'
            || options.CallbackPath.IndexOfAny(new[] { '?', '#', '\r', '\n', '\0' }) >= 0
            || options.CallbackPath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("A bounded absolute callback path is required.", nameof(options.CallbackPath));
        }

        if (options.LoginTimeout < TimeSpan.FromSeconds(10) || options.LoginTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(options.LoginTimeout));
        }

        _ = options.BuildAuthorizationUri ?? throw new ArgumentException("An authorization URL builder is required.");
        _ = options.ExchangeAsync ?? throw new ArgumentException("An authorization exchange is required.");
    }

    private static async Task<LoopbackAuthorizationResult> ReadCallbackAsync(
        TcpClient client,
        string expectedHost,
        string expectedPath,
        string? expectedState,
        CancellationToken cancellationToken)
    {
        using (client)
        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await ReadRequestAsync(client.GetStream(), requestTimeout.Token).ConfigureAwait(false);
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal)
                || !string.Equals(request.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
                || request.HasBody)
            {
                await WriteResponseAsync(client, 400, "Invalid OAuth callback request.", requestTimeout.Token)
                    .ConfigureAwait(false);
                throw new InvalidCallbackException();
            }

            if (!Uri.TryCreate("http://" + expectedHost + request.Target, UriKind.Absolute, out var target)
                || !FixedTimeEquals(target.AbsolutePath, expectedPath))
            {
                await WriteResponseAsync(client, 404, "OAuth callback route not found.", requestTimeout.Token)
                    .ConfigureAwait(false);
                throw new InvalidCallbackException();
            }

            Dictionary<string, string> query;
            try
            {
                query = ParseQuery(target.Query);
            }
            catch (InvalidOperationException)
            {
                await WriteResponseAsync(client, 400, "Invalid OAuth callback query.", requestTimeout.Token)
                    .ConfigureAwait(false);
                throw new InvalidCallbackException();
            }

            if (expectedState is not null
                && (!query.TryGetValue("state", out var state) || !FixedTimeEquals(state, expectedState)))
            {
                await WriteResponseAsync(client, 400, "OAuth callback state mismatch.", requestTimeout.Token)
                    .ConfigureAwait(false);
                throw new InvalidCallbackException();
            }

            if (query.TryGetValue("error", out var error))
            {
                await WriteResponseAsync(client, 400, "OAuth authorization was denied.", requestTimeout.Token)
                    .ConfigureAwait(false);
                return new LoopbackAuthorizationResult(null, Bound(error, 4096));
            }

            if (!query.TryGetValue("code", out var code) || !IsBoundedValue(code, 65_536))
            {
                await WriteResponseAsync(client, 400, "OAuth callback omitted its code.", requestTimeout.Token)
                    .ConfigureAwait(false);
                throw new InvalidCallbackException();
            }

            await WriteResponseAsync(
                client,
                200,
                "Authorization received. Return to the application to finish signing in.",
                requestTimeout.Token).ConfigureAwait(false);
            return new LoopbackAuthorizationResult(code, null);
        }
    }

    private static async Task<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (buffer.Length < MaximumRequestBytes)
        {
            var read = await BoundedOAuthHttp.WaitAsync(
                stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            var bytes = buffer.GetBuffer();
            var count = checked((int)buffer.Length);
            if (HeaderEnd(bytes, count) >= 0)
            {
                break;
            }
        }

        var length = checked((int)buffer.Length);
        var headerEnd = HeaderEnd(buffer.GetBuffer(), length);
        if (headerEnd < 0 || length > MaximumRequestBytes)
        {
            throw new InvalidCallbackException();
        }

        var bytesRead = buffer.ToArray();
        if (bytesRead.Take(headerEnd).Any(value => value is > 0x7f or 0))
        {
            throw new InvalidCallbackException();
        }

        var headerText = Encoding.ASCII.GetString(bytesRead, 0, headerEnd);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3
            || requestLine[1].Length > MaximumRequestTargetBytes
            || requestLine[2] is not ("HTTP/1.0" or "HTTP/1.1")
            || lines.Length - 1 > MaximumHeaders)
        {
            throw new InvalidCallbackException();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0
                || !headers.TryAdd(line.Substring(0, separator).Trim(), line.Substring(separator + 1).Trim()))
            {
                throw new InvalidCallbackException();
            }
        }

        if (!headers.TryGetValue("Host", out var host) || !IsBoundedValue(host, 512))
        {
            throw new InvalidCallbackException();
        }

        var hasBody = headers.ContainsKey("Transfer-Encoding")
                      || headers.TryGetValue("Content-Length", out var contentLength)
                      && !string.Equals(contentLength, "0", StringComparison.Ordinal);
        return new HttpRequest(requestLine[0], requestLine[1], host, hasBody);
    }

    private static async Task WriteResponseAsync(
        TcpClient client,
        int status,
        string message,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Error",
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n"
            + $"Content-Length: {body.Length}\r\n\r\n");
        var stream = client.GetStream();
        await BoundedOAuthHttp.WaitAsync(
            stream.WriteAsync(header, 0, header.Length, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await BoundedOAuthHttp.WaitAsync(
            stream.WriteAsync(body, 0, body.Length, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static int HeaderEnd(byte[] bytes, int count)
    {
        for (var index = 0; index <= count - 4; index++)
        {
            if (bytes[index] == '\r'
                && bytes[index + 1] == '\n'
                && bytes[index + 2] == '\r'
                && bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        if (query.Length > 16_384)
        {
            throw new InvalidOperationException("The OAuth callback query exceeded its safety bound.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 32)
        {
            throw new InvalidOperationException("The OAuth callback query contains too many fields.");
        }

        foreach (var part in parts)
        {
            var pieces = part.Split(new[] { '=' }, 2);
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(pieces[0]);
                value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            }
            catch (UriFormatException exception)
            {
                throw new InvalidOperationException("The OAuth callback query is invalid.", exception);
            }

            if (!IsBoundedValue(key, 256) || value.Length > 65_536 || !result.TryAdd(key, value))
            {
                throw new InvalidOperationException("The OAuth callback query contains an invalid field.");
            }
        }

        return result;
    }

    private static bool IsBoundedValue(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum
        && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;

    private static string Bound(string value, int maximum) =>
        IsBoundedValue(value, maximum)
            ? value
            : throw new InvalidOperationException("An OAuth callback field exceeded its safety bound.");

    private static string RandomToken(int byteCount)
    {
        var bytes = new byte[byteCount];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return Base64Url(bytes);
    }

    private static byte[] Sha256(byte[] value)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(value);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class LoopbackListener : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _expectedHost;
        private readonly string _expectedPath;
        private readonly string? _expectedState;
        private int _stopped;

        private LoopbackListener(
            TcpListener listener,
            string expectedHost,
            string expectedPath,
            string? expectedState,
            Uri redirectUri)
        {
            _listener = listener;
            _expectedHost = expectedHost;
            _expectedPath = expectedPath;
            _expectedState = expectedState;
            RedirectUri = redirectUri;
        }

        public Uri RedirectUri { get; }

        public static async Task<LoopbackListener> StartAsync(
            string redirectHost,
            int requestedPort,
            string path,
            string? expectedState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 1; ; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, requestedPort);
                try
                {
                    listener.Start(8);
                    var endpoint = (IPEndPoint)listener.LocalEndpoint;
                    var authority = redirectHost + ":" + endpoint.Port;
                    var redirect = new Uri("http://" + authority + path, UriKind.Absolute);
                    return new LoopbackListener(listener, authority, path, expectedState, redirect);
                }
                catch (SocketException exception)
                    when (requestedPort != 0
                        && exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                        && attempt < MaximumBindAttempts)
                {
                    listener.Stop();
                    await Task.Delay(BindRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    listener.Stop();
                    throw;
                }
            }
        }

        public async Task<LoopbackAuthorizationResult> WaitForCallbackAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                TcpClient client;
                using (cancellationToken.Register(Stop))
                {
                    try
                    {
                        client = await BoundedOAuthHttp.WaitAsync(
                            _listener.AcceptTcpClientAsync(),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    catch (SocketException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                try
                {
                    return await ReadCallbackAsync(
                        client,
                        _expectedHost,
                        _expectedPath,
                        _expectedState,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidCallbackException)
                {
                }
            }

            throw new InvalidOperationException("The OAuth loopback listener rejected too many invalid callbacks.");
        }

        public Task StopAsync()
        {
            Stop();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return default;
        }

        private void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _listener.Stop();
            }
        }
    }

    private sealed class InvalidCallbackException : Exception
    {
    }

    private sealed class LoopbackAuthorizationResult
    {
        public LoopbackAuthorizationResult(string? code, string? error)
        {
            Code = code;
            Error = error;
        }

        public string? Code { get; }

        public string? Error { get; }
    }

    private sealed class HttpRequest
    {
        public HttpRequest(string method, string target, string host, bool hasBody)
        {
            Method = method;
            Target = target;
            Host = host;
            HasBody = hasBody;
        }

        public string Method { get; }

        public string Target { get; }

        public string Host { get; }

        public bool HasBody { get; }
    }
}
