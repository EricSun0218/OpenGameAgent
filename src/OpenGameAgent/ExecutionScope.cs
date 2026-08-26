using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

/// <summary>
/// Stable capability names that a host may grant to one input. The core Agent loop and ordinary
/// host tools remain available in every scope; optional capabilities require an explicit grant
/// when the scope is restricted.
/// </summary>
public static class GameExecutionCapabilities
{
    public const string PersistentPlanning = "opengameagent.persistent-planning";

    /// <summary>
    /// Allows an installed behavior-learning extension to accept bounded learning proposals for
    /// this input. The grant never permits a model to activate a proposal or expand its tools.
    /// </summary>
    public const string BehaviorLearning = "opengameagent.behavior-learning";
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
    /// Runs the core Agent loop while withholding every optional capability grant.
    /// </summary>
    public static GameExecutionScope NoOptionalCapabilities { get; } = new(false, Array.Empty<string>());

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
/// Resolves a trusted execution scope before any extension contributes context or tools. Server
/// hosts should derive the result from their authenticated principal and policy.
/// </summary>
public delegate ValueTask<GameExecutionScope> GameExecutionScopeProvider(
    GameInput input,
    CancellationToken cancellationToken);
