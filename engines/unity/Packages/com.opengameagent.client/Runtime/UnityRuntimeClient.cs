using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenGameAgent.Unity.Protocol;
using UnityEngine.Networking;

namespace OpenGameAgent.Unity
{
    public enum RuntimeClientFailureKind
    {
        PreDispatch,
        Transport,
        Http,
        Protocol,
    }

    public readonly struct RuntimeClientResult<T>
    {
        private RuntimeClientResult(T value, RuntimeClientFailure failure, bool succeeded)
        {
            Value = value;
            Failure = failure;
            Succeeded = succeeded;
        }

        public bool Succeeded { get; }

        public T Value { get; }

        public RuntimeClientFailure Failure { get; }

        public static RuntimeClientResult<T> Success(T value)
        {
            return new RuntimeClientResult<T>(value, default, true);
        }

        public static RuntimeClientResult<T> Failed(RuntimeClientFailure failure)
        {
            return new RuntimeClientResult<T>(default, failure, false);
        }
    }

    public readonly struct RuntimeClientFailure
    {
        internal RuntimeClientFailure(RuntimeClientFailureKind kind, long status, string message)
        {
            Kind = kind;
            Status = status;
            Message = message;
        }

        public RuntimeClientFailureKind Kind { get; }

        public long Status { get; }

        public string Message { get; }
    }

    public sealed class UnityRuntimeClient
    {
        private readonly string baseUrl;
        private readonly string accessToken;
        private readonly int maxRequestBytes;
        private readonly int maxResponseBytes;
        private readonly int maxAttachmentResponseBytes;
        private readonly int timeoutSeconds;

        public UnityRuntimeClient(
            string baseUrl,
            string accessToken,
            int maxRequestBytes,
            int maxResponseBytes,
            int maxAttachmentResponseBytes,
            int timeoutSeconds)
        {
            if (baseUrl == null
                || baseUrl.IndexOf('\\') >= 0
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri parsedBaseUrl)
                || !string.IsNullOrEmpty(parsedBaseUrl.UserInfo)
                || !string.IsNullOrEmpty(parsedBaseUrl.Query)
                || !string.IsNullOrEmpty(parsedBaseUrl.Fragment)
                || !(string.Equals(parsedBaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(parsedBaseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        && IsLoopbackHost(parsedBaseUrl.Host))))
            {
                throw new ArgumentException("The Runtime base URL must use HTTPS or loopback HTTP without credentials, query, fragment, or backslashes.", nameof(baseUrl));
            }
            if (!IsValidAccessToken(accessToken)) throw new ArgumentException("The Runtime access token is invalid.", nameof(accessToken));
            if (maxRequestBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRequestBytes));
            if (maxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
            if (maxAttachmentResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttachmentResponseBytes));
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            this.baseUrl = parsedBaseUrl.AbsoluteUri.TrimEnd('/');
            this.accessToken = accessToken ?? string.Empty;
            this.maxRequestBytes = maxRequestBytes;
            this.maxResponseBytes = maxResponseBytes;
            this.maxAttachmentResponseBytes = maxAttachmentResponseBytes;
            this.timeoutSeconds = timeoutSeconds;
        }

        public IEnumerator Initialize(Action<RuntimeClientResult<RuntimeInitializeResponse>> completed)
        {
            return Post<object, RuntimeInitializeResponse>("/runtime/v1/initialize", null, completed);
        }

        public IEnumerator SubmitRun(
            RuntimeRunRequest request,
            Action<RuntimeClientResult<RuntimeRunAccepted>> completed)
        {
            return Post<RuntimeRunRequest, RuntimeRunAccepted>("/runtime/v1/runs", request, completed);
        }

        public IEnumerator ReadDeferred(
            RuntimeDeferredReadRequest request,
            Action<RuntimeClientResult<RuntimeDeferredReadResponse>> completed)
        {
            return Post<RuntimeDeferredReadRequest, RuntimeDeferredReadResponse>(
                "/runtime/v1/deferred/read",
                request,
                completed);
        }

        public IEnumerator CloseAuthority(
            RuntimeAuthorityCloseRequest request,
            Action<RuntimeClientResult<RuntimeAuthorityClosure>> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            string expected = RuntimeProtocolJson.Serialize(request);
            return Post<RuntimeAuthorityCloseRequest, RuntimeAuthorityClosure>(
                "/runtime/v1/authority/close", request, result =>
                {
                    if (result.Succeeded)
                    {
                        ValidateAuthorityClosure(result.Value);
                        var closureRequest = new RuntimeAuthorityCloseRequest
                        {
                            SessionId = result.Value.SessionId, ActorId = result.Value.ActorId,
                            Scope = result.Value.Scope, Generation = result.Value.Generation,
                            OperationId = result.Value.OperationId, GoalRetirement = result.Value.GoalRetirement,
                            WorkRetirement = result.Value.WorkRetirement,
                        };
                        if (!string.Equals(expected, RuntimeProtocolJson.Serialize(closureRequest), StringComparison.Ordinal))
                            throw new InvalidDataException("The Runtime authority closure does not match the requested operation.");
                    }
                    completed(result);
                });
        }

        public IEnumerator ReadAuthorityClosure(
            RuntimeAuthorityReadRequest request,
            Action<RuntimeClientResult<RuntimeAuthorityReadResponse>> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            string expected = RuntimeProtocolJson.Serialize(request);
            return Post<RuntimeAuthorityReadRequest, RuntimeAuthorityReadResponse>(
                "/runtime/v1/authority/read", request, result =>
                {
                    if (result.Succeeded && result.Value.Closure != null)
                    {
                        RuntimeAuthorityClosure closure = result.Value.Closure;
                        ValidateAuthorityClosure(closure);
                        var key = new RuntimeAuthorityReadRequest
                        {
                            SessionId = closure.SessionId, ActorId = closure.ActorId,
                            Scope = closure.Scope, Generation = closure.Generation,
                        };
                        if (!string.Equals(expected, RuntimeProtocolJson.Serialize(key), StringComparison.Ordinal))
                            throw new InvalidDataException("The Runtime authority closure does not match the requested key.");
                    }
                    completed(result);
                });
        }

        private static void ValidateAuthorityClosure(RuntimeAuthorityClosure closure)
        {
            if (closure == null || string.IsNullOrEmpty(closure.Scope)
                || string.IsNullOrEmpty(closure.OperationId) || string.IsNullOrEmpty(closure.Generation)
                || (closure.Generation.Length > 1 && closure.Generation[0] == '0'))
                throw new InvalidDataException("The Runtime authority closure is malformed.");
            ValidateAuthorityDecimal(closure.Generation);
            ValidateAuthorityTimestamp(closure.ClosedAt);
            if ((closure.WorkRetirement == null) != (closure.WorkRetirementResult == null))
                throw new InvalidDataException("The Work retirement request and result must occur together.");
            if (closure.WorkRetirement != null)
            {
                RuntimeAuthorityWorkRetirement work = closure.WorkRetirement;
                RuntimeAuthorityWorkRetirementPartition lane = work.Partition;
                if (lane == null || !IsAuthorityIdentifier(work.RetirementId) || !IsAuthorityIdentifier(lane.OwnerId)
                    || !IsAuthorityIdentifier(lane.TimelineId) || !IsAuthorityIdentifier(lane.SessionId) || !IsAuthorityIdentifier(lane.ActorId)
                    || lane.SessionId != closure.SessionId || lane.ActorId != closure.ActorId)
                    throw new InvalidDataException("The Work retirement does not identify the closed stream's lane.");
                ValidateAuthorityDecimal(lane.Generation);
                ValidateAuthorityTimestamp(work.RecordedAt);
                RuntimeAuthorityWorkRetirementResult result = closure.WorkRetirementResult;
                if (result.Phase != Phase.Fenced && result.Phase != Phase.Finalized)
                    throw new InvalidDataException("The Work retirement phase is malformed.");
                ValidateAuthorityDecimal(result.CancelledScheduleCount);
                ValidateAuthorityDecimal(result.CancelledOccurrenceCount);
                ValidateAuthorityDecimal(result.CancelledMessageCount);
            }
        }

        private static bool IsAuthorityIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char character in value) if (char.IsControl(character)) return false;
            return true;
        }

        private static void ValidateAuthorityDecimal(string value)
        {
            if (string.IsNullOrEmpty(value) || (value.Length > 1 && value[0] == '0'))
                throw new InvalidDataException("The Runtime authority decimal is malformed.");
            foreach (char digit in value)
                if (digit < '0' || digit > '9') throw new InvalidDataException("The Runtime authority decimal is malformed.");
        }

        private static void ValidateAuthorityTimestamp(string value)
        {
            if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                throw new InvalidDataException("The Runtime authority timestamp is malformed.");
        }

        private static JToken ParseAuthorityJson(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JToken.ReadFrom(reader);
        }

        public IEnumerator ResumeDeferred(
            RuntimeDeferredCommandRequest request,
            Action<RuntimeClientResult<RuntimeRunAccepted>> completed)
        {
            return Post<RuntimeDeferredCommandRequest, RuntimeRunAccepted>(
                "/runtime/v1/deferred/resume",
                request,
                completed);
        }

        public IEnumerator AbandonDeferred(
            RuntimeDeferredCommandRequest request,
            Action<RuntimeClientResult<RuntimeDeferredAbandonResponse>> completed)
        {
            return Post<RuntimeDeferredCommandRequest, RuntimeDeferredAbandonResponse>(
                "/runtime/v1/deferred/abandon",
                request,
                completed);
        }

        public IEnumerator ListApprovals(
            RuntimeApprovalListRequest request,
            Action<RuntimeClientResult<RuntimeApprovalListResponse>> completed)
        {
            return Post<RuntimeApprovalListRequest, RuntimeApprovalListResponse>(
                "/runtime/v1/approvals",
                request,
                completed);
        }

        public IEnumerator DecideApproval(
            RuntimeApprovalDecisionRequest request,
            Action<RuntimeClientResult<RuntimeApprovalDecisionResponse>> completed)
        {
            return Post<RuntimeApprovalDecisionRequest, RuntimeApprovalDecisionResponse>(
                "/runtime/v1/approvals/decision",
                request,
                completed);
        }

        public IEnumerator NextAction(
            RuntimeActionNextRequest request,
            Action<RuntimeClientResult<RuntimeActionNextResponse>> completed)
        {
            return Post<RuntimeActionNextRequest, RuntimeActionNextResponse>(
                "/runtime/v1/actions/next",
                request,
                completed);
        }

        public IEnumerator RecoverAction(
            RuntimeActionRecoveryRequest request,
            Action<RuntimeClientResult<RuntimeActionRecoveryResponse>> completed)
        {
            return Post<RuntimeActionRecoveryRequest, RuntimeActionRecoveryResponse>(
                "/runtime/v1/actions/recovery",
                request,
                result =>
                {
                    RuntimeActionRecoveryDelivery delivery = result.Succeeded ? result.Value.Delivery : null;
                    RuntimeActionRecord action = delivery == null ? null : delivery.Action;
                    if (result.Succeeded
                        && delivery != null
                        && (delivery.Kind != RuntimeActionRecoveryDeliveryKind.Reconcile
                            || action == null
                            || action.Claim == null
                            || (action.Status != RuntimeActionRecordStatus.Claimed
                                && action.Status != RuntimeActionRecordStatus.Uncertain)
                            || action.GoalProjectionSettlement != null
                            || !string.Equals(action.Claim.Attempt, action.Attempt, StringComparison.Ordinal)
                            || !string.Equals(action.SessionId, request.SessionId, StringComparison.Ordinal)
                            || !string.Equals(action.ActorId, request.ActorId, StringComparison.Ordinal)))
                    {
                        completed(RuntimeClientResult<RuntimeActionRecoveryResponse>.Failed(
                            new RuntimeClientFailure(
                                RuntimeClientFailureKind.Protocol,
                                200,
                                "The recovery-only action response violates the authoritative state boundary.")));
                        return;
                    }
                    completed(result);
                });
        }

        public IEnumerator ReconcileAction(
            RuntimeActionReconcileRequest request,
            Action<RuntimeClientResult<RuntimeActionRecordResponse>> completed)
        {
            return Post<RuntimeActionReconcileRequest, RuntimeActionRecordResponse>(
                "/runtime/v1/actions/reconcile",
                request,
                completed);
        }

        public IEnumerator GetAction(
            RuntimeActionGetRequest request,
            Action<RuntimeClientResult<RuntimeActionGetResponse>> completed)
        {
            return Post<RuntimeActionGetRequest, RuntimeActionGetResponse>(
                "/runtime/v1/actions/get",
                request,
                completed);
        }

        public IEnumerator RecordActionReceipt(
            RuntimeActionReceiptRequest request,
            Action<RuntimeClientResult<RuntimeActionRecordResponse>> completed)
        {
            return Post<RuntimeActionReceiptRequest, RuntimeActionRecordResponse>(
                "/runtime/v1/actions/receipt",
                request,
                completed);
        }

        public IEnumerator ReadAttachment(
            RuntimeAttachmentReadRequest attachmentRequest,
            Action<RuntimeClientResult<byte[]>> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            RuntimeAttachmentReadRequestReference reference = attachmentRequest == null ? null : attachmentRequest.Reference;
            if (!IsValidAttachmentRequest(attachmentRequest, reference, maxAttachmentResponseBytes))
            {
                completed(RuntimeClientResult<byte[]>.Failed(
                    new RuntimeClientFailure(
                        RuntimeClientFailureKind.PreDispatch,
                        0,
                        "The attachment request is invalid.")));
                yield break;
            }

            byte[] upload;
            try
            {
                upload = SerializeRequestBody(attachmentRequest);
            }
            catch (Exception exception)
            {
                completed(RuntimeClientResult<byte[]>.Failed(new RuntimeClientFailure(
                    RuntimeClientFailureKind.PreDispatch,
                    0,
                    exception.Message)));
                yield break;
            }

            UnityWebRequest request;
            try
            {
                request = CreatePost(
                    "/runtime/v1/attachments/read",
                    upload,
                    maxAttachmentResponseBytes,
                    maxResponseBytes);
            }
            catch (Exception exception)
            {
                completed(RuntimeClientResult<byte[]>.Failed(new RuntimeClientFailure(
                    RuntimeClientFailureKind.PreDispatch,
                    0,
                    exception.Message)));
                yield break;
            }

            using (request)
            {
                var download = (BoundedDownloadHandler)request.downloadHandler;
                UnityWebRequestAsyncOperation operation;
                try
                {
                    operation = request.SendWebRequest();
                }
                catch (Exception exception)
                {
                    completed(RuntimeClientResult<byte[]>.Failed(new RuntimeClientFailure(
                        RuntimeClientFailureKind.PreDispatch,
                        0,
                        exception.Message)));
                    yield break;
                }
                yield return operation;
                if (IsRedirectResponse(request, baseUrl + "/runtime/v1/attachments/read"))
                {
                    completed(RuntimeClientResult<byte[]>.Failed(
                        new RuntimeClientFailure(
                            RuntimeClientFailureKind.Http,
                            request.responseCode,
                            "Runtime redirects are not allowed.")));
                    yield break;
                }
                if (download.InvalidResponseStatus)
                {
                    completed(RuntimeClientResult<byte[]>.Failed(
                        new RuntimeClientFailure(
                            RuntimeClientFailureKind.Transport,
                            request.responseCode,
                            "Runtime attachment response body arrived before a valid response status.")));
                    yield break;
                }
                if (download.LimitExceeded)
                {
                    completed(RuntimeClientResult<byte[]>.Failed(
                        new RuntimeClientFailure(
                            FailureKindForResponse(request),
                            request.responseCode,
                            "Runtime attachment response exceeded its configured byte limit.")));
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string text = download.Text;
                    completed(RuntimeClientResult<byte[]>.Failed(
                        new RuntimeClientFailure(
                            FailureKindForResponse(request),
                            request.responseCode,
                            string.IsNullOrEmpty(text) ? request.error : text)));
                    yield break;
                }

                byte[] data = download.Bytes;
                string validationError = ValidateAttachmentResponse(request, reference, data);
                completed(validationError == null
                    ? RuntimeClientResult<byte[]>.Success(data)
                    : RuntimeClientResult<byte[]>.Failed(new RuntimeClientFailure(
                        RuntimeClientFailureKind.Protocol,
                        request.responseCode,
                        validationError)));
            }
        }

        private IEnumerator Post<TRequest, TResponse>(
            string path,
            TRequest body,
            Action<RuntimeClientResult<TResponse>> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            byte[] upload;
            try
            {
                upload = body == null ? Array.Empty<byte>() : SerializeRequestBody(body);
            }
            catch (Exception exception)
            {
                return CompletePostFailure(completed, exception);
            }

            return PostCaptured(path, upload, completed);
        }

        private static IEnumerator CompletePostFailure<TResponse>(
            Action<RuntimeClientResult<TResponse>> completed,
            Exception exception)
        {
            completed(RuntimeClientResult<TResponse>.Failed(new RuntimeClientFailure(
                RuntimeClientFailureKind.PreDispatch, 0, exception.Message)));
            yield break;
        }

        private IEnumerator PostCaptured<TResponse>(
            string path,
            byte[] upload,
            Action<RuntimeClientResult<TResponse>> completed)
        {
            UnityWebRequest request;
            try
            {
                request = CreatePost(path, upload, maxResponseBytes, maxResponseBytes);
            }
            catch (Exception exception)
            {
                completed(RuntimeClientResult<TResponse>.Failed(new RuntimeClientFailure(
                    RuntimeClientFailureKind.PreDispatch,
                    0,
                    exception.Message)));
                yield break;
            }

            using (request)
            {
                var download = (BoundedDownloadHandler)request.downloadHandler;
                UnityWebRequestAsyncOperation operation;
                try
                {
                    operation = request.SendWebRequest();
                }
                catch (Exception exception)
                {
                    completed(RuntimeClientResult<TResponse>.Failed(new RuntimeClientFailure(
                        RuntimeClientFailureKind.PreDispatch,
                        0,
                        exception.Message)));
                    yield break;
                }
                yield return operation;

                if (IsRedirectResponse(request, baseUrl + path))
                {
                    completed(RuntimeClientResult<TResponse>.Failed(
                        new RuntimeClientFailure(
                            RuntimeClientFailureKind.Http,
                            request.responseCode,
                            "Runtime redirects are not allowed.")));
                    yield break;
                }
                if (download.InvalidResponseStatus)
                {
                    completed(RuntimeClientResult<TResponse>.Failed(
                        new RuntimeClientFailure(
                            RuntimeClientFailureKind.Transport,
                            request.responseCode,
                            "Runtime response body arrived before a valid response status.")));
                    yield break;
                }
                if (download.LimitExceeded)
                {
                    completed(RuntimeClientResult<TResponse>.Failed(
                        new RuntimeClientFailure(
                            FailureKindForResponse(request),
                            request.responseCode,
                            "Runtime response exceeded its configured byte limit.")));
                    yield break;
                }

                string text = download.Text;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    completed(RuntimeClientResult<TResponse>.Failed(
                        new RuntimeClientFailure(
                            FailureKindForResponse(request),
                            request.responseCode,
                            string.IsNullOrEmpty(text) ? request.error : text)));
                    yield break;
                }

                try
                {
                    TResponse response = RuntimeProtocolJson.Deserialize<TResponse>(text);
                    if ((typeof(TResponse) == typeof(RuntimeAuthorityClosure) || typeof(TResponse) == typeof(RuntimeAuthorityReadResponse))
                        && !JToken.DeepEquals(ParseAuthorityJson(text), ParseAuthorityJson(RuntimeProtocolJson.Serialize(response))))
                        throw new InvalidDataException("The Runtime authority response did not preserve its JSON types and fields.");
                    completed(RuntimeClientResult<TResponse>.Success(response));
                }
                catch (Exception exception)
                {
                    completed(RuntimeClientResult<TResponse>.Failed(
                        new RuntimeClientFailure(
                            RuntimeClientFailureKind.Protocol,
                            request.responseCode,
                            exception.Message)));
                }
            }
        }

        private static RuntimeClientFailureKind FailureKindForResponse(UnityWebRequest request)
        {
            if (request.responseCode >= 300) return RuntimeClientFailureKind.Http;
            return request.responseCode == 0
                ? RuntimeClientFailureKind.Transport
                : RuntimeClientFailureKind.Protocol;
        }

        private byte[] SerializeRequestBody(object body)
        {
            string json = RuntimeProtocolJson.Serialize(body);
            if (Encoding.UTF8.GetByteCount(json) > maxRequestBytes)
            {
                throw new InvalidDataException("Runtime request body exceeded its configured byte limit.");
            }
            return Encoding.UTF8.GetBytes(json);
        }

        private UnityWebRequest CreatePost(
            string path,
            byte[] upload,
            int successMaxResponseBytes,
            int errorMaxResponseBytes)
        {
            var request = new UnityWebRequest(baseUrl + path, UnityWebRequest.kHttpVerbPOST)
            {
                timeout = timeoutSeconds,
                redirectLimit = 0,
            };
            request.downloadHandler = new BoundedDownloadHandler(
                successMaxResponseBytes,
                errorMaxResponseBytes,
                () => request.responseCode);
            if (upload.Length > 0)
            {
                request.uploadHandler = new UploadHandlerRaw(upload);
                request.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(accessToken)) request.SetRequestHeader("Authorization", "Bearer " + accessToken);
            return request;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            string unbracketed = host == null ? string.Empty : host.Trim('[', ']');
            if (!IPAddress.TryParse(unbracketed, out IPAddress address)) return false;
            if (IPAddress.IPv6Loopback.Equals(address)) return true;
            byte[] bytes = address.GetAddressBytes();
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes[0] == 127;
        }

        private static bool IsValidAccessToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            bool sawTokenCharacter = false;
            bool sawPadding = false;
            foreach (char character in value)
            {
                if (character == '=')
                {
                    if (!sawTokenCharacter) return false;
                    sawPadding = true;
                    continue;
                }
                bool alphaNumeric = (character >= 'A' && character <= 'Z')
                    || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9');
                bool tokenCharacter = alphaNumeric
                    || character == '-'
                    || character == '.'
                    || character == '_'
                    || character == '~'
                    || character == '+'
                    || character == '/';
                if (sawPadding || !tokenCharacter) return false;
                sawTokenCharacter = true;
            }
            return sawTokenCharacter;
        }

        private static int ResponseLimitForStatus(long status, int successMaxBytes, int errorMaxBytes)
        {
            return status >= 200 && status <= 299 ? successMaxBytes : status > 0 ? errorMaxBytes : 0;
        }

        private static bool IsRedirectResponse(UnityWebRequest request, string expectedUrl)
        {
            if (request.responseCode >= 300 && request.responseCode <= 399) return true;
            return !Uri.TryCreate(request.url, UriKind.Absolute, out Uri actual)
                || !Uri.TryCreate(expectedUrl, UriKind.Absolute, out Uri expected)
                || !string.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal);
        }

        private static bool IsValidAttachmentRequest(
            RuntimeAttachmentReadRequest request,
            RuntimeAttachmentReadRequestReference reference,
            int maxBytes)
        {
            return request != null
                && reference != null
                && !string.IsNullOrEmpty(request.SessionId)
                && !string.IsNullOrEmpty(request.ActorId)
                && reference.Type == ReferenceType.ImageReference
                && reference.MediaType == MediaType.ImagePng
                && reference.Bytes > 0
                && reference.Bytes <= maxBytes
                && reference.Width > 0
                && reference.Height > 0
                && IsAttachmentId(reference.AttachmentId);
        }

        private static bool IsAttachmentId(string value)
        {
            if (value == null || value.Length != 68 || !value.StartsWith("img_", StringComparison.Ordinal)) return false;
            for (int index = 4; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }
            return true;
        }

        private static string ValidateAttachmentResponse(
            UnityWebRequest request,
            RuntimeAttachmentReadRequestReference reference,
            byte[] data)
        {
            string contentLengthText = request.GetResponseHeader("Content-Length");
            if (string.IsNullOrEmpty(contentLengthText)
                || contentLengthText[0] == '0'
                || !long.TryParse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out long contentLength)
                || contentLength != reference.Bytes
                || data.LongLength != reference.Bytes
                || !string.Equals(NormalizeMediaType(request.GetResponseHeader("Content-Type")), "image/png", StringComparison.Ordinal)
                || !string.Equals(request.GetResponseHeader("ETag"), "\"" + reference.AttachmentId + "\"", StringComparison.Ordinal)
                || !string.Equals(request.GetResponseHeader("Cache-Control"), "private, no-store", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.GetResponseHeader("X-Content-Type-Options"), "nosniff", StringComparison.OrdinalIgnoreCase))
            {
                return "The Runtime attachment response metadata is invalid.";
            }

            byte[] digest;
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = sha256.ComputeHash(data);
            }
            string attachmentId = "img_" + BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
            string contentDigest = "sha-256=:" + Convert.ToBase64String(digest) + ":";
            if (!string.Equals(reference.AttachmentId, attachmentId, StringComparison.Ordinal)
                || !string.Equals(request.GetResponseHeader("Content-Digest"), contentDigest, StringComparison.Ordinal))
            {
                return "The Runtime attachment response content does not match its reference.";
            }
            return null;
        }

        private static string NormalizeMediaType(string value)
        {
            if (value == null) return null;
            int separator = value.IndexOf(';');
            return (separator < 0 ? value : value.Substring(0, separator)).Trim().ToLowerInvariant();
        }

        private sealed class BoundedDownloadHandler : DownloadHandlerScript
        {
            private readonly int successMaxBytes;
            private readonly int errorMaxBytes;
            private readonly Func<long> responseStatus;
            private readonly MemoryStream bytes = new MemoryStream();

            public BoundedDownloadHandler(int successMaxBytes, int errorMaxBytes, Func<long> responseStatus)
            {
                this.successMaxBytes = successMaxBytes;
                this.errorMaxBytes = errorMaxBytes;
                this.responseStatus = responseStatus;
            }

            public bool LimitExceeded { get; private set; }

            public bool InvalidResponseStatus { get; private set; }

            public string Text => Encoding.UTF8.GetString(bytes.ToArray());

            public byte[] Bytes => bytes.ToArray();

            protected override void ReceiveContentLengthHeader(ulong contentLength)
            {
                int maxBytes = ResponseLimitForStatus(responseStatus(), successMaxBytes, errorMaxBytes);
                if (maxBytes > 0 && contentLength > (ulong)maxBytes) LimitExceeded = true;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return true;
                int maxBytes = ResponseLimitForStatus(responseStatus(), successMaxBytes, errorMaxBytes);
                if (maxBytes == 0)
                {
                    InvalidResponseStatus = true;
                    return false;
                }
                if (LimitExceeded || bytes.Length + dataLength > maxBytes)
                {
                    LimitExceeded = true;
                    return false;
                }

                bytes.Write(data, 0, dataLength);
                return true;
            }

        }
    }
}
