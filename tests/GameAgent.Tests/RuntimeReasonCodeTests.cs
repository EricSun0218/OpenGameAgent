using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeReasonCodeTests
{
    [Fact]
    public void ProviderFailureMetadataIsBoundedAtConstruction()
    {
        var maximum = new string(
            'r',
            ProtocolLimits.MaxRuntimeEventReasonCodeUnicodeScalars);
        var accepted = new ProviderException(
            maximum,
            new string('c', 96),
            new string('m', 2_048),
            retryable: false,
            usageKnownToBeZero: true);

        Assert.Equal(maximum, accepted.Code);
        Assert.True(accepted.UsageKnownToBeZero);
        Assert.Throws<ArgumentException>(
            () => new ProviderException(
                string.Empty,
                "provider",
                "safe",
                retryable: false));
        Assert.Throws<RuntimeContentLimitException>(
            () => new ProviderException(
                maximum + "x",
                "provider",
                "safe",
                retryable: false));
        Assert.Throws<RuntimeContentLimitException>(
            () => new ProviderException(
                "failure",
                new string('c', 97),
                "safe",
                retryable: false));
        Assert.Throws<RuntimeContentLimitException>(
            () => new ProviderException(
                "failure",
                "provider",
                new string('m', 2_049),
                retryable: false));
    }

    [Fact]
    public void PolicyReasonCodesMatchRuntimeEventWireCapacity()
    {
        var maximum = new string(
            'r',
            ProtocolLimits.MaxRuntimeEventReasonCodeUnicodeScalars);

        Assert.Equal(
            maximum,
            ToolDisclosureDecision.Deny(maximum).ReasonCode);
        Assert.Equal(
            maximum,
            SkillAdmissionDecision.Deny(maximum).ReasonCode);
        Assert.Throws<RuntimeContentLimitException>(
            () => ToolDisclosureDecision.Deny(maximum + "x"));
        Assert.Throws<RuntimeContentLimitException>(
            () => SkillAdmissionDecision.Deny(maximum + "x"));
        Assert.Throws<RuntimeContentLimitException>(
            () => new SkillAdmissionException(maximum + "x"));
    }
}
