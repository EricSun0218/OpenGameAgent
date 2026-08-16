using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenGameAgent.Extensions;

internal static class StoredExtensionStateReader
{
    public static IReadOnlyDictionary<string, string> Read(
        GameSessionSnapshot session,
        string extensionId)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        var prefix = Uri.EscapeDataString(extensionId) + ":";
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in session.ExtensionState)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair.Key.Substring(prefix.Length));
            if (!state.TryAdd(key, pair.Value))
            {
                throw new InvalidOperationException(
                    $"Extension state contains duplicate decoded key '{key}'.");
            }
        }

        return new ReadOnlyDictionary<string, string>(state);
    }
}
