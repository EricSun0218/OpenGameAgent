using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class ProviderModelCatalogTests
{
    [Fact]
    public void SelectsOnlyRoutesThatDeclareEveryRequirement()
    {
        var catalog = ProviderModelCatalog.Capture(new IStreamingModelProvider[]
        {
            new CatalogProvider(
                "basic",
                new ProviderCapabilities
                {
                    Streaming = true,
                    TextInput = true,
                    StructuredInput = false,
                    ToolCalling = true,
                    MaxTools = 8,
                    MaxOutputTokens = 4_096
                }),
            new CatalogProvider(
                "capable",
                new ProviderCapabilities
                {
                    Streaming = true,
                    TextInput = true,
                    StructuredInput = true,
                    ToolCalling = true,
                    ParallelToolCalls = true,
                    ReasoningEffort = true,
                    MaxTools = 128,
                    MaxOutputTokens = 32_768
                })
        });

        var selected = catalog.Select(new ProviderCapabilityRequirements
        {
            ToolCalling = true,
            StructuredInput = true,
            ParallelToolCalls = true,
            ReasoningEffort = true,
            MinimumTools = 32,
            MinimumOutputTokens = 8_192
        });
        var evaluated = catalog.Evaluate(new ProviderCapabilityRequirements
        {
            StructuredInput = true,
            MinimumTools = 32
        });

        Assert.Single(selected);
        Assert.Equal("capable", selected[0].ProviderId);
        var basic = Assert.Single(evaluated, item =>
            item.Route.ProviderId == "basic");
        Assert.Contains(ProviderCapabilityCodes.StructuredInput,
            basic.MissingCapabilities);
        Assert.Contains(ProviderCapabilityCodes.ToolCount,
            basic.MissingCapabilities);
    }

    [Fact]
    public void ReturnedCapabilitiesCannotMutateCatalog()
    {
        var catalog = ProviderModelCatalog.Capture(new[]
        {
            new CatalogProvider(
                "route",
                new ProviderCapabilities
                {
                    Streaming = true,
                    TextInput = true,
                    ToolCalling = true
                })
        });

        catalog.Routes[0].Capabilities.ToolCalling = false;

        Assert.True(catalog.Find("route")!.Capabilities.ToolCalling);
    }

    private sealed class CatalogProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private readonly ProviderCapabilities _capabilities;

        internal CatalogProvider(
            string providerId,
            ProviderCapabilities capabilities)
        {
            ProviderId = providerId;
            _capabilities = capabilities;
            RouteMetadata = new ProviderRouteMetadata(
                "model-1",
                new ProviderDialectContract(
                    "catalog-test.v1",
                    ProviderRequestFamily.Custom,
                    "catalog-test.request.v1",
                    ProviderStreamFraming.Custom,
                    "catalog-test.stream.v1",
                    "catalog-test.tools.v1",
                    "catalog-test.usage.v1",
                    "catalog-test.reasoning.v1",
                    "application/json"));
        }

        public string ProviderId { get; }

        public ProviderCapabilities Capabilities => _capabilities.Clone();

        public ProviderRouteMetadata RouteMetadata { get; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
