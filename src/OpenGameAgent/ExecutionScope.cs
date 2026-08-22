using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

/// <summary>
/// Stable capability names that a host may grant to one input. Core QuickResponse and bounded
/// short-agent execution remain available in every scope; optional durable capabilities require
/// an explicit grant when the scope is restricted.
/// </summary>
public static class GameExecutionCapabilities
{
    public const string PersistentPlanning = "opengameagent.persistent-planning";
}

/// <summary>
/// A host-derived capability scope for one input. This is runtime configuration, not model-visible
/// metadata, so a model or remote payload cannot grant itself an optional capability.
/// </summary>
public sealed class GameExecutionScope
{
    private readonly IReadOnlyCollection<string> _grantedCapabilities;

    private GameExecutionScope(bool unrestricted, IEnumerable<string> grantedCapabilities)
    {
        IsUnrestricted = unrestricted;
        var copied = (grantedCapabilities ?? throw new ArgumentNullException(nameof(grantedCapabilities)))
            .Select(capability => RequireCapability(capability, nameof(grantedCapabilities)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
        if (copied.Length > 64)
        {
            throw new ArgumentException("An execution scope cannot grant more than 64 capabilities.", nameof(grantedCapabilities));
        }

        _grantedCapabilities = new ReadOnlyCollection<string>(copied);
    }

    /// <summary>
    /// Preserves the default runtime behavior and allows every registered optional capability.
    /// </summary>
    public static GameExecutionScope Unrestricted { get; } = new(true, Array.Empty<string>());

    /// <summary>
    /// Allows automatic QuickResponse/short-agent routing while withholding every optional grant.
    /// </summary>
    public static GameExecutionScope ShortTaskOnly { get; } = new(false, Array.Empty<string>());

    public bool IsUnrestricted { get; }

    public IReadOnlyCollection<string> GrantedCapabilities => _grantedCapabilities;

    public static GameExecutionScope Restricted(IEnumerable<string> grantedCapabilities) =>
        new(false, grantedCapabilities);

    public bool Allows(string capability)
    {
        var required = RequireCapability(capability, nameof(capability));
        return IsUnrestricted || _grantedCapabilities.Contains(required, StringComparer.Ordinal);
    }

    internal static string RequireCapability(string capability, string parameterName)
    {
        var required = GameJson.RequireId(capability, parameterName);
        if (required.Length > 256 || required.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An execution capability must be a bounded printable identifier.",
                parameterName);
        }

        return required;
    }
}

/// <summary>
/// Resolves a trusted execution scope before any extension contributes context, tools, or pending
/// work. Server hosts should derive the result from their authenticated principal and policy.
/// </summary>
public delegate ValueTask<GameExecutionScope> GameExecutionScopeProvider(
    GameInput input,
    CancellationToken cancellationToken);

public sealed class GameExecutionCapabilityDeniedException : InvalidOperationException
{
    public GameExecutionCapabilityDeniedException(string capability)
        : base(CreateMessage(capability))
    {
        Capability = capability;
    }

    public string Capability { get; }

    private static string CreateMessage(string capability)
    {
        var validated = GameExecutionScope.RequireCapability(capability, nameof(capability));
        return $"The host did not grant execution capability '{validated}'.";
    }
}
