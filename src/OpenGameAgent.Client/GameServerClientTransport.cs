using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Client;

internal static class GameServerClientTransport
{
    public static void ValidateBaseUri(
        Uri? value,
        bool allowInsecureHttp,
        string parameterName,
        string invalidMessage,
        string insecureMessage)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.UserInfo.Length > 0
            || value.Query.Length > 0
            || value.Fragment.Length > 0
            || (!string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(invalidMessage, parameterName);
        }

        if (string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !value.IsLoopback
            && !allowInsecureHttp)
        {
            throw new ArgumentException(insecureMessage, parameterName);
        }
    }

    public static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage();
            return request.Headers.TryAddWithoutValidation(name, "value");
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool ContainsInvalidCredentialCharacters(
        string? value,
        int maximumCharacters,
        bool rejectAllControlCharacters = false)
    {
        if ((value?.Length ?? 0) > maximumCharacters)
        {
            return true;
        }

        if (value is null)
        {
            return false;
        }

        if (!rejectAllControlCharacters)
        {
            return value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    public static Uri CreateEndpoint(Uri baseUri, string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.Relative, out _))
        {
            throw new ArgumentException("A server endpoint path must be relative.", parameterName);
        }

        var normalized = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        var endpoint = new Uri(normalized, path);
        if (!string.Equals(endpoint.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != baseUri.Port)
        {
            throw new ArgumentException("A server endpoint cannot change the configured server origin.", parameterName);
        }

        return endpoint;
    }

    public static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = new char[Math.Min(4096, maximumCharacters)];
        var result = new StringBuilder();
        while (result.Length < maximumCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(
                buffer,
                0,
                Math.Min(buffer.Length, maximumCharacters - result.Length)).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            result.Append(buffer, 0, read);
        }

        if (await reader.ReadAsync(buffer, 0, 1).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("The server response exceeded the configured size limit.");
        }

        return result.ToString();
    }

    public static async IAsyncEnumerable<string> ReadBoundedLinesAsync(
        StreamReader reader,
        int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumCharacters + 1));
        var line = new StringBuilder(Math.Min(4096, maximumCharacters));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    cancellationToken.IsCancellationRequested
                    && exception is ObjectDisposedException or IOException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (read == 0)
                {
                    if (line.Length > 0)
                    {
                        yield return TrimCarriageReturn(line);
                    }

                    yield break;
                }

                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        yield return TrimCarriageReturn(line);
                        line.Clear();
                        continue;
                    }

                    line.Append(buffer[index]);
                    if (line.Length > maximumCharacters)
                    {
                        throw new InvalidDataException("A server event line exceeded the configured size limit.");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    public static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }
    }

    private static string TrimCarriageReturn(StringBuilder line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
        {
            length--;
        }

        return line.ToString(0, length);
    }
}
