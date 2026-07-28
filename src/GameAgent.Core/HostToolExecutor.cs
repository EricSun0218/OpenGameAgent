using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal sealed class HostToolExecutor : IToolCallExecutor
{
    private readonly IGameHost _host;
    private readonly IReadOnlyDictionary<string, ActionRequest> _requests;
    private readonly ToolArgumentValidator _resultValidator;
    private readonly ConcurrentDictionary<string, ActionReceipt> _receipts =
        new(StringComparer.Ordinal);

    public HostToolExecutor(
        IGameHost host,
        IReadOnlyDictionary<string, ActionRequest> requests,
        ToolArgumentValidator? resultValidator = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _requests = requests
                    ?? throw new ArgumentNullException(nameof(requests));
        _resultValidator = resultValidator ?? new ToolArgumentValidator();
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
        var hostReceipt = await _host
            .SubmitActionAsync(hostRequest, cancellationToken)
            .ConfigureAwait(false);
        var receipt = ActionReceiptIngressValidator.ValidateAndClone(
            action,
            hostReceipt);
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
}
