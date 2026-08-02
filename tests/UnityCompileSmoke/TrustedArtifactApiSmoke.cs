using System;
using GameAgent.Core;
using GameAgent.Generation;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Providers.Anthropic;
using GameAgent.Providers.OpenAICompatible;
using GameAgent.Providers.MediaHttp;
using GameAgent.Runtime;
using GameAgent.Workflow;

namespace GameAgent.Unity
{
    internal static class TrustedArtifactApiSmoke
    {
        public static Type[] ShippedApiTypes()
        {
            return new[]
            {
                typeof(AgentRun),
                typeof(HeadlessAgentRuntimeLimits),
                typeof(GenerationRuntime),
                typeof(FileJournalOptions),
                typeof(AnthropicProviderOptions),
                typeof(OpenAiCompatibleProviderOptions),
                typeof(MediaHttpGenerationProvider),
                typeof(GameAgentRuntimeBuilder),
                typeof(WorkflowCompiler)
            };
        }
    }
}
