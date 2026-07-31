using GameAgent.Core;

namespace GameAgent.Runtime;

/// <summary>
/// Default structural admission for world agent jobs. Schema validation is
/// still performed by the runtime; game-specific evidence requirements can
/// replace this policy.
/// </summary>
public sealed class WorldAgentFinalOutputAdmissionPolicy
    : IFinalOutputAdmissionPolicy
{
    public string PolicyId => "world_agent_structural_admission";

    public string Version => "1";

    public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
        FinalOutputAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var run = request.Run;
        if (!run.Extensions.TryGetValue(
                WorldAgentRuntimeBridge.JobExtensionName,
                out var job)
            || job.ValueKind != System.Text.Json.JsonValueKind.Object
            || !job.TryGetProperty("contract", out var contract)
            || !string.Equals(
                contract.GetString(),
                "game-agent.world-agent-job.v1",
                StringComparison.Ordinal)
            || !GameContextEnvelope.TryRead(run, out var coordinate)
            || coordinate is null)
        {
            return new ValueTask<FinalOutputAdmissionDecision>(
                FinalOutputAdmissionDecision.Reject(
                    "world_agent_binding_missing"));
        }

        var jobDigest = CanonicalJsonDigest.ComputeSha256(job);
        var boundContext = request.Context.FirstOrDefault(
            candidate => candidate.Required
                         && !candidate.CanDefer
                         && string.Equals(
                             candidate.Category,
                             "world_agent_job",
                             StringComparison.Ordinal)
                         && candidate.Content.HasValue
                         && string.Equals(
                             CanonicalJsonDigest.ComputeSha256(
                                 candidate.Content.Value),
                             jobDigest,
                             StringComparison.Ordinal));
        if (boundContext is null)
        {
            return new ValueTask<FinalOutputAdmissionDecision>(
                FinalOutputAdmissionDecision.Reject(
                    "world_agent_context_unbound"));
        }

        return new ValueTask<FinalOutputAdmissionDecision>(
            FinalOutputAdmissionDecision.Accept(
                "world_agent_output_admitted"));
    }
}
