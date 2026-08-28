using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace OpenGameAgent.Unity
{
    /// <summary>One bounded server-sent event delivered by the runtime service.</summary>
    public sealed class GameAgentStreamEvent
    {
        internal GameAgentStreamEvent(string id, string name, string json)
        {
            Id = id;
            Name = name;
            Json = json;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Json { get; private set; }
    }

    /// <summary>A safe transport failure that never reflects a response body.</summary>
    public sealed class GameAgentClientException : Exception
    {
        internal GameAgentClientException(long statusCode, string category)
            : base("Game Agent server request failed (" + statusCode + ", " + category + ").")
        {
            StatusCode = statusCode;
            Category = category;
        }

        public long StatusCode { get; private set; }
        public string Category { get; private set; }
    }

    /// <summary>
    /// Native UnityWebRequest client for a separately hosted OpenGameAgent runtime.
    /// It owns transport only: models, tools, memory, planning, and durable actions stay in the service.
    /// </summary>
    public sealed class OpenGameAgentClient : IDisposable
    {
        private const int MinimumLimit = 1024;
        private readonly string _baseUrl;
        private readonly Func<string> _authenticationJsonProvider;
        private readonly int _maximumRequestBytes;
        private readonly int _maximumResponseBytes;
        private readonly int _maximumEventBytes;
        private readonly int _maximumPendingEvents;
        private bool _disposed;

        public OpenGameAgentClient(
            string baseUrl,
            Func<string> authenticationJsonProvider = null,
            int maximumRequestBytes = 1024 * 1024,
            int maximumResponseBytes = 4 * 1024 * 1024,
            int maximumEventBytes = 1024 * 1024,
            int maximumPendingEvents = 256)
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _authenticationJsonProvider = authenticationJsonProvider;
            _maximumRequestBytes = Bounded(maximumRequestBytes, MinimumLimit, 64 * 1024 * 1024, "maximumRequestBytes");
            _maximumResponseBytes = Bounded(maximumResponseBytes, MinimumLimit, 64 * 1024 * 1024, "maximumResponseBytes");
            _maximumEventBytes = Bounded(maximumEventBytes, MinimumLimit, 8 * 1024 * 1024, "maximumEventBytes");
            _maximumPendingEvents = Bounded(maximumPendingEvents, 1, 4096, "maximumPendingEvents");
        }

        public Task<string> ReadCapabilitiesAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync(UnityWebRequest.kHttpVerbGET, "v1/capabilities", null, false, cancellationToken);
        }

        public Task<string> PostJsonAsync(string path, string bodyObjectJson, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync(UnityWebRequest.kHttpVerbPOST, path, bodyObjectJson, true, cancellationToken);
        }

        public Task<byte[]> ReadAttachmentAsync(string sessionJson, string attachmentId, CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireObject(sessionJson, "sessionJson");
            RequireIdentifier(attachmentId, "attachmentId");
            string body = "{\"session\":" + sessionJson + ",\"attachmentId\":" + Quote(attachmentId) + "}";
            return SendBytesAsync("v1/sessions/attachments/read", body, cancellationToken);
        }

        public Task RunAsync(
            string inputJson,
            Func<GameAgentStreamEvent, Task> handler,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            RequireObject(inputJson, "inputJson");
            if (handler == null) throw new ArgumentNullException("handler");
            if (runId != null) RequireIdentifier(runId, "runId");
            string body = runId == null
                ? "{\"input\":" + inputJson + "}"
                : "{\"input\":" + inputJson + ",\"runId\":" + Quote(runId) + "}";
            return StreamAsync("v1/runs/stream", body, handler, cancellationToken);
        }

        public Task StreamActionsAsync(
            string sessionJson,
            Func<GameAgentStreamEvent, Task> handler,
            CancellationToken cancellationToken = default(CancellationToken),
            int maximum = 1)
        {
            RequireObject(sessionJson, "sessionJson");
            if (handler == null) throw new ArgumentNullException("handler");
            Bounded(maximum, 1, 32, "maximum");
            string body = "{\"session\":" + sessionJson + ",\"maximum\":" + maximum + "}";
            return StreamAsync("v1/actions/stream", body, handler, cancellationToken);
        }

        public Task<bool> SteerAsync(
            string sessionJson,
            string expectedRunCoordinateJson,
            string inputJson,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ControlAsync("steer", sessionJson, expectedRunCoordinateJson, inputJson, cancellationToken);
        }

        public Task<bool> FollowUpAsync(
            string sessionJson,
            string expectedRunCoordinateJson,
            string inputJson,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ControlAsync("follow-up", sessionJson, expectedRunCoordinateJson, inputJson, cancellationToken);
        }

        public Task<bool> AbortAsync(
            string sessionJson,
            string expectedRunCoordinateJson,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ControlAsync("abort", sessionJson, expectedRunCoordinateJson, null, cancellationToken);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private async Task<bool> ControlAsync(
            string operation,
            string sessionJson,
            string expectedRunCoordinateJson,
            string inputJson,
            CancellationToken cancellationToken)
        {
            RequireObject(sessionJson, "sessionJson");
            RequireObject(expectedRunCoordinateJson, "expectedRunCoordinateJson");
            if (inputJson != null) RequireObject(inputJson, "inputJson");
            string body = "{\"session\":" + sessionJson + ",\"expected\":" + expectedRunCoordinateJson
                + (inputJson == null ? string.Empty : ",\"input\":" + inputJson) + "}";
            string response = await PostJsonAsync("v1/control/" + operation, body, cancellationToken);
            return ContainsTrueBoolean(response, "accepted");
        }

        private async Task<string> SendAsync(
            string method,
            string path,
            string bodyObjectJson,
            bool includeAuthentication,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            string body = bodyObjectJson;
            if (body != null)
            {
                RequireObject(body, "bodyObjectJson");
                if (includeAuthentication) body = AddAuthentication(body);
                EnsureSize(body, _maximumRequestBytes, "Client request is too large.");
            }

            using (UnityWebRequest request = new UnityWebRequest(Endpoint(path), method))
            {
                request.redirectLimit = 0;
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.uploadHandler = new UploadHandlerRaw(bytes);
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                await SendRequestAsync(request, cancellationToken);
                EnsureSuccess(request);
                byte[] data = request.downloadHandler.data;
                if (data == null) return string.Empty;
                if (data.Length > _maximumResponseBytes) throw new InvalidDataException("Server response is too large.");
                return Encoding.UTF8.GetString(data);
            }
        }

        private async Task<byte[]> SendBytesAsync(string path, string bodyObjectJson, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            string body = AddAuthentication(bodyObjectJson);
            EnsureSize(body, _maximumRequestBytes, "Client request is too large.");
            using (UnityWebRequest request = new UnityWebRequest(Endpoint(path), UnityWebRequest.kHttpVerbPOST))
            {
                request.redirectLimit = 0;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                await SendRequestAsync(request, cancellationToken);
                EnsureSuccess(request);
                byte[] data = request.downloadHandler.data ?? new byte[0];
                if (data.Length > _maximumResponseBytes) throw new InvalidDataException("Server response is too large.");
                return data;
            }
        }

        private async Task StreamAsync(
            string path,
            string bodyObjectJson,
            Func<GameAgentStreamEvent, Task> handler,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            string body = AddAuthentication(bodyObjectJson);
            EnsureSize(body, _maximumRequestBytes, "Client request is too large.");
            var download = new BoundedSseDownloadHandler(_maximumEventBytes, _maximumPendingEvents);
            using (UnityWebRequest request = new UnityWebRequest(Endpoint(path), UnityWebRequest.kHttpVerbPOST))
            {
                request.redirectLimit = 0;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = download;
                request.SetRequestHeader("Content-Type", "application/json");
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                using (cancellationToken.Register(request.Abort))
                {
                    while (!operation.isDone)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await DrainAsync(download, handler);
                        await Task.Yield();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    await DrainAsync(download, handler);
                }
                EnsureSuccess(request);
                download.ThrowIfInvalid();
            }
        }

        private static async Task DrainAsync(BoundedSseDownloadHandler source, Func<GameAgentStreamEvent, Task> handler)
        {
            GameAgentStreamEvent item;
            while (source.TryDequeue(out item)) await handler(item);
            source.ThrowIfInvalid();
        }

        private static async Task SendRequestAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            using (cancellationToken.Register(request.Abort))
            {
                while (!operation.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private string AddAuthentication(string bodyObjectJson)
        {
            RequireObject(bodyObjectJson, "bodyObjectJson");
            string authentication = _authenticationJsonProvider == null ? null : _authenticationJsonProvider();
            if (string.IsNullOrWhiteSpace(authentication)) return bodyObjectJson;
            RequireObject(authentication, "authenticationJson");
            string trimmed = bodyObjectJson.Trim();
            if (trimmed == "{}") return "{\"authentication\":" + authentication.Trim() + "}";
            return trimmed.Substring(0, trimmed.Length - 1) + ",\"authentication\":" + authentication.Trim() + "}";
        }

        private void EnsureSuccess(UnityWebRequest request)
        {
            if (request.responseCode >= 300 && request.responseCode < 400)
                throw new InvalidDataException("Redirects are not allowed.");
            if (request.result == UnityWebRequest.Result.Success) return;
            string category = request.responseCode > 0 ? "http-" + request.responseCode : "transport";
            throw new GameAgentClientException(request.responseCode, category);
        }

        private string Endpoint(string path)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Invalid endpoint path.", "path");
            return _baseUrl + path.TrimStart('/');
        }

        private static string NormalizeBaseUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("The server URL must be absolute and must not contain credentials, query, or fragment.", "baseUrl");
            bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
            if (uri.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
                throw new ArgumentException("Remote Game Agent servers require HTTPS; HTTP is restricted to loopback.", "baseUrl");
            string result = uri.AbsoluteUri;
            return result.EndsWith("/", StringComparison.Ordinal) ? result : result + "/";
        }

        private static int Bounded(int value, int minimum, int maximum, string name)
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
            return value;
        }

        private static void RequireObject(string json, string name)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("A JSON object is required.", name);
            string trimmed = json.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                throw new ArgumentException("A JSON object is required.", name);
        }

        private static void RequireIdentifier(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("A bounded identifier is required.", name);
            for (int index = 0; index < value.Length; index++)
                if (char.IsControl(value[index])) throw new ArgumentException("Identifiers cannot contain control characters.", name);
        }

        private static void EnsureSize(string value, int maximumBytes, string message)
        {
            if (Encoding.UTF8.GetByteCount(value) > maximumBytes) throw new InvalidDataException(message);
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder(value.Length + 2);
            result.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 32) result.Append("\\u" + ((int)character).ToString("x4"));
                        else result.Append(character);
                        break;
                }
            }
            result.Append('"');
            return result.ToString();
        }

        private static bool ContainsTrueBoolean(string json, string property)
        {
            string compact = json.Replace(" ", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Replace("\t", string.Empty);
            return compact.IndexOf("\"" + property + "\":true", StringComparison.Ordinal) >= 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException("OpenGameAgentClient");
        }

        private sealed class BoundedSseDownloadHandler : DownloadHandlerScript
        {
            private readonly object _gate = new object();
            private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
            private readonly StringBuilder _pending = new StringBuilder();
            private readonly Queue<GameAgentStreamEvent> _events = new Queue<GameAgentStreamEvent>();
            private readonly int _maximumEventBytes;
            private readonly int _maximumPendingEvents;
            private Exception _error;

            internal BoundedSseDownloadHandler(int maximumEventBytes, int maximumPendingEvents)
                : base(new byte[8192])
            {
                _maximumEventBytes = maximumEventBytes;
                _maximumPendingEvents = maximumPendingEvents;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return true;
                lock (_gate)
                {
                    if (_error != null) return false;
                    try
                    {
                        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(dataLength)];
                        int count = _decoder.GetChars(data, 0, dataLength, characters, 0, false);
                        _pending.Append(characters, 0, count);
                        ParseFrames();
                        EnsureSize(_pending.ToString(), _maximumEventBytes, "Server event is too large.");
                        return true;
                    }
                    catch (Exception error)
                    {
                        _error = error;
                        return false;
                    }
                }
            }

            protected override void CompleteContent()
            {
                lock (_gate)
                {
                    if (_error != null) return;
                    try
                    {
                        char[] characters = new char[4];
                        int count = _decoder.GetChars(new byte[0], 0, 0, characters, 0, true);
                        _pending.Append(characters, 0, count);
                        ParseFrames();
                        if (_pending.ToString().Trim().Length != 0)
                            throw new InvalidDataException("Server stream ended with an incomplete event.");
                    }
                    catch (Exception error)
                    {
                        _error = error;
                    }
                }
            }

            internal bool TryDequeue(out GameAgentStreamEvent item)
            {
                lock (_gate)
                {
                    if (_events.Count == 0)
                    {
                        item = null;
                        return false;
                    }
                    item = _events.Dequeue();
                    return true;
                }
            }

            internal void ThrowIfInvalid()
            {
                lock (_gate)
                    if (_error != null) throw new InvalidDataException("Invalid Game Agent event stream.", _error);
            }

            private void ParseFrames()
            {
                string text = _pending.ToString();
                while (true)
                {
                    int lf = text.IndexOf("\n\n", StringComparison.Ordinal);
                    int crlf = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (lf < 0 && crlf < 0) break;
                    bool useCrlf = crlf >= 0 && (lf < 0 || crlf < lf);
                    int index = useCrlf ? crlf : lf;
                    int delimiterLength = useCrlf ? 4 : 2;
                    string frame = text.Substring(0, index);
                    EnsureSize(frame, _maximumEventBytes, "Server event is too large.");
                    GameAgentStreamEvent item = ParseFrame(frame);
                    if (item != null)
                    {
                        if (_events.Count >= _maximumPendingEvents) throw new InvalidDataException("Server event queue is full.");
                        _events.Enqueue(item);
                    }
                    text = text.Substring(index + delimiterLength);
                }
                _pending.Length = 0;
                _pending.Append(text);
            }

            private static GameAgentStreamEvent ParseFrame(string frame)
            {
                string id = string.Empty;
                string name = "message";
                var data = new StringBuilder();
                string[] lines = frame.Replace("\r", string.Empty).Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (line.StartsWith("id:", StringComparison.Ordinal)) id = line.Substring(3).TrimStart();
                    else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line.Substring(6).TrimStart();
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(5).TrimStart());
                    }
                }
                return data.Length == 0 ? null : new GameAgentStreamEvent(id, name, data.ToString());
            }
        }
    }
}
