using System;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Providers.OpenAICompatible;
using GameAgent.Runtime;

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
                typeof(FileJournalOptions),
                typeof(OpenAiCompatibleProviderOptions),
                typeof(GameAgentRuntimeBuilder)
            };
        }
    }
}
