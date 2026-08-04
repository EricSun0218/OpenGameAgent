using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Generation;

namespace GameAgent.Unity.Tests
{
    public static class UnityGenerationGateScenario
    {
        public static GenerationRuntime CreateRuntime()
        {
            return new GenerationRuntime(
                new IGenerationProvider[] { new EchoGenerationProvider() },
                new InMemoryGenerationJobStore(),
                new RejectingArtifactStore());
        }

        public static GenerationRequest CreateRequest(string operationId)
        {
            using (var document = JsonDocument.Parse(
                       "{\"npc\":\"merchant-1\",\"temperature\":3.5,"+
                       "\"signals\":[1,true,{\"kind\":\"month_elapsed\"}]}"))
            {
                return new GenerationRequest
                {
                    OperationId = operationId,
                    Modality = GenerationModalities.StructuredContent,
                    Input = document.RootElement.Clone(),
                    IdempotencyKey = operationId,
                    AuthorityId = "world-1"
                };
            }
        }

        private sealed class EchoGenerationProvider : IGenerationProvider
        {
            public string Name
            {
                get { return "unity-test"; }
            }

            public GenerationProviderCapabilities Capabilities { get; } =
                new GenerationProviderCapabilities
                {
                    Modalities = new[]
                    {
                        GenerationModalities.StructuredContent
                    },
                    SupportsPolling = true,
                    SupportsCancellation = true
                };

            public ValueTask<GenerationSubmission> SubmitAsync(
                GenerationRequest request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<GenerationSubmission>(
                    new GenerationSubmission
                    {
                        Acceptance = GenerationAcceptance.Accepted,
                        Result = new GenerationProviderResult
                        {
                            Status = GenerationJobStatuses.Succeeded,
                            ProviderJobId = request.OperationId + "-provider",
                            Progress = 1,
                            Output = request.Input.Clone()
                        }
                    });
            }

            public ValueTask<GenerationProviderResult> GetAsync(
                string providerJobId,
                string modality,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<GenerationProviderResult>(
                    new GenerationProviderResult
                    {
                        Status = GenerationJobStatuses.Succeeded,
                        ProviderJobId = providerJobId,
                        Progress = 1
                    });
            }

            public ValueTask<GenerationCancelResult> CancelAsync(
                string providerJobId,
                string modality,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<GenerationCancelResult>(
                    new GenerationCancelResult
                    {
                        Accepted = true,
                        Status = GenerationJobStatuses.CancelRequested
                    });
            }
        }

        private sealed class RejectingArtifactStore : IGenerationArtifactStore
        {
            public ValueTask<GenerationArtifact> ImportAsync(
                string operationId,
                int ordinal,
                GenerationArtifactSource source,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException(
                    "The Unity generation gate does not emit artifacts.");
            }
        }
    }
}
