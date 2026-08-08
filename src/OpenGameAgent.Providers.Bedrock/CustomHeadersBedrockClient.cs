using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.Internal;

namespace OpenGameAgent.Providers.Bedrock;

internal sealed class CustomHeadersBedrockClient : AmazonBedrockRuntimeClient
{
    public CustomHeadersBedrockClient(
        AmazonBedrockRuntimeConfig config,
        IReadOnlyDictionary<string, string> headers)
        : base(config)
    {
        AddCustomHeadersHandler(headers);
    }

    public CustomHeadersBedrockClient(
        AWSCredentials credentials,
        AmazonBedrockRuntimeConfig config,
        IReadOnlyDictionary<string, string> headers)
        : base(credentials, config)
    {
        AddCustomHeadersHandler(headers);
    }

    private void AddCustomHeadersHandler(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count > 0)
        {
            RuntimePipeline.AddHandlerBefore<Signer>(new CustomHeadersHandler(headers));
        }
    }

    private sealed class CustomHeadersHandler : PipelineHandler
    {
        private readonly IReadOnlyDictionary<string, string> _headers;

        public CustomHeadersHandler(IReadOnlyDictionary<string, string> headers)
        {
            _headers = headers;
        }

        public override void InvokeSync(IExecutionContext executionContext)
        {
            AwsBedrockTransport.ApplyHeaders(executionContext.RequestContext.Request.Headers, _headers);
            base.InvokeSync(executionContext);
        }

        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            AwsBedrockTransport.ApplyHeaders(executionContext.RequestContext.Request.Headers, _headers);
            return base.InvokeAsync<T>(executionContext);
        }
    }
}
