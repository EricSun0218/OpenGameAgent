using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal sealed class HostToolExecutor : IToolCallExecutor
{
    private readonly IGameHost _host;
    private readonly IReadOnlyDictionary<string, ActionRequest> _requests;
    private readonly AgentRun _run;
    private readonly ToolArgumentValidator _resultValidator;
    private readonly bool _requireAudienceIncarnation;
    private readonly Action<ActionRequest, GameActionProgress>?
        _progressPublisher;
    private readonly ConcurrentDictionary<string, ActionReceipt> _receipts =
        new(StringComparer.Ordinal);

    public HostToolExecutor(
        IGameHost host,
        IReadOnlyDictionary<string, ActionRequest> requests,
        AgentRun run,
        bool requireAudienceIncarnation = false,
        ToolArgumentValidator? resultValidator = null,
        Action<ActionRequest, GameActionProgress>? progressPublisher = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _requests = requests
                    ?? throw new ArgumentNullException(nameof(requests));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _requireAudienceIncarnation = requireAudienceIncarnation;
        _resultValidator = resultValidator ?? new ToolArgumentValidator();
        _progressPublisher = progressPublisher;
    }

    public bool TryGetReceipt(
        string toolCallId,
        out ActionReceipt? receipt)
    {
        return _receipts.TryGetValue(toolCallId, out receipt);
    }

    public async ValueTask<JsonElement> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!_requests.TryGetValue(request.ToolCallId, out var action))
        {
            throw new InvalidOperationException(
                "A journaled action request is missing.");
        }

        var hostRequest = ProtocolJson.DeserializeActionRequest(
            ProtocolJson.Serialize(action));
        ActionReceipt hostReceipt;
        if (_host is IProgressReportingGameHost progressHost
            && _progressPublisher is not null)
        {
            var sink = new ScopedProgressSink(
                action,
                _progressPublisher);
            try
            {
                hostReceipt = await progressHost
                    .SubmitActionAsync(
                        hostRequest,
                        sink,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                sink.Close();
            }
        }
        else
        {
            hostReceipt = await _host
                .SubmitActionAsync(hostRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        var receipt = ActionReceiptIngressValidator.ValidateAndClone(
            action,
            hostReceipt,
            _run,
            _requireAudienceIncarnation);
        if (string.Equals(
                receipt.Status,
                ReceiptStatuses.Succeeded,
                StringComparison.Ordinal)
            && request.Tool.ResultSchema.HasValue)
        {
            var result = receipt.Result
                         ?? ProtocolJson.ParseElement("null");
            var validation = _resultValidator.Validate(
                request.Tool.ResultSchema.Value,
                result);
            if (!validation.IsValid)
            {
                receipt.Result = null;
                receipt.ErrorCode = "tool_result_schema_invalid";
                receipt.Retryable = false;
            }
        }

        if (!_receipts.TryAdd(request.ToolCallId, receipt))
        {
            throw new InvalidOperationException(
                "The host completed a tool call more than once.");
        }

        return receipt.Result?.Clone() ?? JsonArrayBuilder.Object(
            ("status", JsonArrayBuilder.String(receipt.Status)),
            ("errorCode", receipt.ErrorCode is null
                ? ProtocolJson.ParseElement("null")
                : JsonArrayBuilder.String(receipt.ErrorCode)));
    }

    private sealed class ScopedProgressSink : IGameActionProgressSink
    {
        private const int MaxReports = 10_000;

        private readonly ActionRequest _request;
        private readonly Action<ActionRequest, GameActionProgress> _publish;
        private int _closed;
        private int _reports;

        public ScopedProgressSink(
            ActionRequest request,
            Action<ActionRequest, GameActionProgress> publish)
        {
            _request = ProtocolJson.DeserializeActionRequest(
                ProtocolJson.Serialize(request));
            _publish = publish;
        }

        public void Report(GameActionProgress progress)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            if (Interlocked.Increment(ref _reports) > MaxReports)
            {
                return;
            }

            var snapshot = (progress
                            ?? throw new ArgumentNullException(
                                nameof(progress)))
                .CloneValidated();
            if (Volatile.Read(ref _closed) == 0)
            {
                _publish(_request, snapshot);
            }
        }

        public void Close()
        {
            Interlocked.Exchange(ref _closed, 1);
        }
    }
}
