using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Core;

public static class PersistentAgentStates
{
    public const string Open = "open";
    public const string Idle = "idle";
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Evicted = "evicted";
    public const string Closed = "closed";
    public const string Failed = "failed";

    internal static bool IsKnown(string value) =>
        value == Open
        || value == Idle
        || value == Waiting
        || value == Running
        || value == Evicted
        || value == Closed
        || value == Failed;

    internal static bool IsTerminal(string value) => value == Closed || value == Failed;
}

public static class AgentContextInheritancePolicies
{
    public const string Full = "full";
    public const string Selected = "selected";
    public const string Summarized = "summarized";
    public const string Empty = "empty";

    internal static bool IsKnown(string value) =>
        value == Full || value == Selected || value == Summarized || value == Empty;
}

public static class PersistentAgentEdgeKinds
{
    public const string ParentChild = "parent_child";
    public const string ActorGroup = "actor_group";
    public const string Peer = "peer";

    internal static bool IsKnown(string value) =>
        value == ParentChild || value == ActorGroup || value == Peer;
}

public static class AgentMailboxMessageStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
}

public sealed class AgentMailboxAcceptance
{
    public string MessageId { get; set; } = string.Empty;

    public string MessageDigest { get; set; } = string.Empty;
}

public sealed class AgentMailboxMessage
{
    public string MessageId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? SenderAgentId { get; set; }

    public string? CoalesceKey { get; set; }

    public string OriginalDigest { get; set; } = string.Empty;

    public IReadOnlyList<AgentMailboxAcceptance> AcceptedMessages { get; set; } =
        Array.Empty<AgentMailboxAcceptance>();

    public long OrderingKey { get; set; }

    public JsonElement Payload { get; set; }

    public string State { get; set; } = AgentMailboxMessageStates.Pending;

    public long Revision { get; set; }
}

public sealed class PersistentAgentNode
{
    public string AgentId { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public string HistoryId { get; set; } = string.Empty;

    public string? ParentAgentId { get; set; }

    public string ContextInheritancePolicy { get; set; } =
        AgentContextInheritancePolicies.Empty;

    public string State { get; set; } = PersistentAgentStates.Open;

    public bool OwnsUnsettledSideEffect { get; set; }

    public long LastAccessOrderingKey { get; set; }

    public IReadOnlyList<AgentMailboxMessage> Mailbox { get; set; } =
        Array.Empty<AgentMailboxMessage>();

    public long Revision { get; set; }
}

public sealed class PersistentAgentEdge
{
    public string EdgeId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string FromAgentId { get; set; } = string.Empty;

    public string ToId { get; set; } = string.Empty;

    public bool Open { get; set; } = true;

    public long Revision { get; set; }
}

public sealed class PersistentAgentGraphState
{
    public IReadOnlyList<PersistentAgentNode> Nodes { get; set; } =
        Array.Empty<PersistentAgentNode>();

    public IReadOnlyList<PersistentAgentEdge> Edges { get; set; } =
        Array.Empty<PersistentAgentEdge>();

    public long Revision { get; set; }
}

public interface IPersistentAgentGraphStore
{
    ValueTask<PersistentAgentGraphState> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> TryPutAsync(
        PersistentAgentGraphState state,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class PersistentAgentGraphOptions
{
    public int MaxAgents { get; set; } = 100_000;

    public int MaxEdges { get; set; } = 400_000;

    public int MaxMailboxMessagesPerAgent { get; set; } = 256;

    public int MaxCommitRetries { get; set; } = 32;

    public int MaxPayloadUtf8Bytes { get; set; } = 262_144;

    internal void Validate()
    {
        if (MaxAgents is < 1 or > 1_000_000
            || MaxEdges is < 1 or > 2_000_000
            || MaxMailboxMessagesPerAgent is < 1 or > 65_536
            || MaxCommitRetries is < 1 or > 1_024
            || MaxPayloadUtf8Bytes is < 1_024
               or > CanonicalJsonDigest.MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PersistentAgentGraphOptions));
        }
    }
}

public sealed class PersistentAgentGraphException : Exception
{
    public PersistentAgentGraphException(
        string reasonCode,
        string message,
        string? agentId = null)
        : base(message)
    {
        ReasonCode = reasonCode;
        AgentId = agentId;
    }

    public string ReasonCode { get; }

    public string? AgentId { get; }
}

public sealed class PersistentAgentGraph
{
    private readonly IPersistentAgentGraphStore _store;
    private readonly PersistentAgentGraphOptions _options;
    private readonly JsonValueLimits _payloadLimits;

    public PersistentAgentGraph(
        IPersistentAgentGraphStore store,
        PersistentAgentGraphOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new PersistentAgentGraphOptions();
        _options.Validate();
        _payloadLimits = new JsonValueLimits(
            _options.MaxPayloadUtf8Bytes,
            maxDepth: 64,
            maxNodes: 65_536,
            maxStringUtf8Bytes: _options.MaxPayloadUtf8Bytes,
            maxContainerItems: 32_768);
    }

    public ValueTask<PersistentAgentNode> RegisterAsync(
        PersistentAgentNode node,
        CancellationToken cancellationToken = default)
    {
        var admitted = SnapshotAndValidate(node);
        return MutateNodeAsync(
            admitted.AgentId,
            create: admitted,
            existing =>
            {
                if (!IdentityEquivalent(existing, admitted))
                {
                    throw new PersistentAgentGraphException(
                        "persistent_agent_identity_conflict",
                        "The agent ID is bound to different durable identity metadata.",
                        admitted.AgentId);
                }

                return existing;
            },
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<PersistentAgentNode>> RegisterManyAsync(
        IEnumerable<PersistentAgentNode> nodes,
        CancellationToken cancellationToken = default)
    {
        if (nodes is null)
        {
            throw new ArgumentNullException(nameof(nodes));
        }

        var admitted = new List<PersistentAgentNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is null || admitted.Count >= _options.MaxAgents)
            {
                throw new PersistentAgentGraphException(
                    "persistent_agent_capacity",
                    "The persistent agent registration batch is invalid or too large.");
            }

            var snapshot = SnapshotAndValidate(node);
            if (!ids.Add(snapshot.AgentId))
            {
                throw new PersistentAgentGraphException(
                    "persistent_agent_identity_conflict",
                    "The persistent agent registration batch contains a duplicate identity.",
                    snapshot.AgentId);
            }

            admitted.Add(snapshot);
        }

        if (admitted.Count == 0)
        {
            return Array.Empty<PersistentAgentNode>();
        }

        for (var attempt = 0; attempt < _options.MaxCommitRetries; attempt++)
        {
            var current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = current.Nodes.ToDictionary(
                item => item.AgentId,
                Snapshot,
                StringComparer.Ordinal);
            foreach (var node in admitted)
            {
                if (existing.TryGetValue(node.AgentId, out var prior))
                {
                    if (!IdentityEquivalent(prior, node))
                    {
                        throw new PersistentAgentGraphException(
                            "persistent_agent_identity_conflict",
                            "A registered agent ID is bound to different identity metadata.",
                            node.AgentId);
                    }

                    continue;
                }

                if (existing.Count >= _options.MaxAgents)
                {
                    throw new PersistentAgentGraphException(
                        "persistent_agent_capacity",
                        "The persistent agent identity store is full.");
                }

                var created = Snapshot(node);
                created.Revision = Math.Max(1, created.Revision);
                existing.Add(created.AgentId, created);
            }

            var next = new PersistentAgentGraphState
            {
                Nodes = new ReadOnlyCollection<PersistentAgentNode>(
                    existing.Values
                        .OrderBy(item => item.AgentId, StringComparer.Ordinal)
                        .Select(Snapshot)
                        .ToArray()),
                Edges = new ReadOnlyCollection<PersistentAgentEdge>(
                    current.Edges.Select(Snapshot).ToArray()),
                Revision = checked(current.Revision + 1)
            };
            if (await _store.TryPutAsync(next, current.Revision, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new ReadOnlyCollection<PersistentAgentNode>(
                    admitted.Select(node => Snapshot(existing[node.AgentId])).ToArray());
            }
        }

        throw Contention();
    }

    public async ValueTask<PersistentAgentNode?> TryGetAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        agentId = Required(agentId, nameof(agentId), 128);
        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var node = state.Nodes.SingleOrDefault(item => item.AgentId == agentId);
        return node is null ? null : Snapshot(node);
    }

    public ValueTask<PersistentAgentNode> TransitionAsync(
        string agentId,
        string state,
        long orderingKey,
        bool ownsUnsettledSideEffect,
        CancellationToken cancellationToken = default)
    {
        agentId = Required(agentId, nameof(agentId), 128);
        if (!PersistentAgentStates.IsKnown(state) || orderingKey < 0)
        {
            throw new ArgumentException("The persistent agent transition is invalid.");
        }

        return MutateNodeAsync(
            agentId,
            create: null,
            existing =>
            {
                if (!CanTransition(existing.State, state))
                {
                    throw new PersistentAgentGraphException(
                        "persistent_agent_transition_invalid",
                        $"Agent state cannot transition from '{existing.State}' to '{state}'.",
                        agentId);
                }

                if (state == PersistentAgentStates.Evicted
                    && (existing.OwnsUnsettledSideEffect || ownsUnsettledSideEffect))
                {
                    throw new PersistentAgentGraphException(
                        "persistent_agent_side_effect_owned",
                        "An agent with an unsettled side effect cannot be evicted.",
                        agentId);
                }

                existing.State = state;
                existing.OwnsUnsettledSideEffect = ownsUnsettledSideEffect;
                existing.LastAccessOrderingKey = Math.Max(
                    existing.LastAccessOrderingKey,
                    orderingKey);
                existing.Revision++;
                return existing;
            },
            cancellationToken);
    }

    public ValueTask<PersistentAgentNode> EnqueueAsync(
        string agentId,
        AgentMailboxMessage message,
        CancellationToken cancellationToken = default)
    {
        agentId = Required(agentId, nameof(agentId), 128);
        var admitted = SnapshotAndValidate(message);
        if (admitted.AcceptedMessages.Count != 0)
        {
            throw new PersistentAgentGraphException(
                "agent_mailbox_acceptance_invalid",
                "A newly enqueued message cannot claim prior accepted identities.",
                agentId);
        }

        return MutateNodeAsync(
            agentId,
            create: null,
            existing =>
            {
                var mailbox = existing.Mailbox.Select(Snapshot).ToList();
                var admittedDigest = MessageDigest(admitted);
                var duplicate = mailbox.SingleOrDefault(item =>
                    item.MessageId == admitted.MessageId);
                if (duplicate is not null)
                {
                    if (duplicate.OriginalDigest != admittedDigest)
                    {
                        throw new PersistentAgentGraphException(
                            "agent_mailbox_message_conflict",
                            "The mailbox message ID is bound to different content.",
                            agentId);
                    }

                    return existing;
                }

                var acceptance = mailbox
                    .SelectMany(item => item.AcceptedMessages)
                    .SingleOrDefault(item => item.MessageId == admitted.MessageId);
                if (acceptance is not null)
                {
                    if (acceptance.MessageDigest != admittedDigest)
                    {
                        throw new PersistentAgentGraphException(
                            "agent_mailbox_message_conflict",
                            "The mailbox message ID is bound to different content.",
                            agentId);
                    }

                    return existing;
                }

                if (admitted.CoalesceKey is not null)
                {
                    var prior = mailbox
                        .Where(item =>
                            item.State == AgentMailboxMessageStates.Pending
                            && item.CoalesceKey == admitted.CoalesceKey)
                        .OrderBy(item => item.OrderingKey)
                        .ThenBy(item => item.MessageId, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (prior is not null)
                    {
                        EnsureMailboxCapacity(mailbox, agentId);
                        prior.AcceptedMessages = new ReadOnlyCollection<AgentMailboxAcceptance>(
                            prior.AcceptedMessages
                                .Append(new AgentMailboxAcceptance
                                {
                                    MessageId = admitted.MessageId,
                                    MessageDigest = admittedDigest
                                })
                                .OrderBy(item => item.MessageId, StringComparer.Ordinal)
                                .Select(Snapshot)
                                .ToArray());
                        prior.Kind = admitted.Kind;
                        prior.SenderAgentId = admitted.SenderAgentId;
                        prior.OrderingKey = Math.Max(
                            prior.OrderingKey,
                            admitted.OrderingKey);
                        prior.Payload = admitted.Payload.Clone();
                        prior.Revision++;
                        existing.Mailbox = SnapshotMailbox(mailbox);
                        existing.Revision++;
                        return existing;
                    }
                }

                EnsureMailboxCapacity(mailbox, agentId);

                mailbox.Add(admitted);
                existing.Mailbox = SnapshotMailbox(mailbox);
                existing.Revision++;
                return existing;
            },
            cancellationToken);
    }

    public ValueTask<PersistentAgentNode> MarkDeliveredAsync(
        string agentId,
        string messageId,
        long expectedMessageRevision,
        CancellationToken cancellationToken = default)
    {
        agentId = Required(agentId, nameof(agentId), 128);
        messageId = Required(messageId, nameof(messageId), 128);
        return MutateNodeAsync(
            agentId,
            create: null,
            existing =>
            {
                var mailbox = existing.Mailbox.Select(Snapshot).ToList();
                var message = mailbox.SingleOrDefault(item => item.MessageId == messageId)
                    ?? throw new PersistentAgentGraphException(
                        "agent_mailbox_message_missing",
                        "The persistent agent mailbox message does not exist.",
                        agentId);
                if (message.State == AgentMailboxMessageStates.Delivered)
                {
                    return existing;
                }

                if (message.Revision != expectedMessageRevision)
                {
                    throw new PersistentAgentGraphException(
                        "agent_mailbox_revision_conflict",
                        "The mailbox message revision changed.",
                        agentId);
                }

                message.State = AgentMailboxMessageStates.Delivered;
                message.Revision++;
                existing.Mailbox = SnapshotMailbox(mailbox);
                existing.Revision++;
                return existing;
            },
            cancellationToken);
    }

    public async ValueTask<PersistentAgentEdge> AddEdgeAsync(
        PersistentAgentEdge edge,
        CancellationToken cancellationToken = default)
    {
        var admitted = SnapshotAndValidate(edge);
        for (var attempt = 0; attempt < _options.MaxCommitRetries; attempt++)
        {
            var current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = current.Edges.SingleOrDefault(item =>
                item.EdgeId == admitted.EdgeId);
            if (existing is not null)
            {
                if (EdgeEquivalent(existing, admitted))
                {
                    return Snapshot(existing);
                }

                throw new PersistentAgentGraphException(
                    "persistent_agent_edge_conflict",
                    "The agent graph edge ID is bound to different content.");
            }

            if (current.Edges.Count >= _options.MaxEdges)
            {
                throw new PersistentAgentGraphException(
                    "persistent_agent_edge_capacity",
                    "The persistent agent graph edge store is full.");
            }

            if (!current.Nodes.Any(node => node.AgentId == admitted.FromAgentId)
                || admitted.Kind == PersistentAgentEdgeKinds.ParentChild
                && !current.Nodes.Any(node => node.AgentId == admitted.ToId))
            {
                throw new PersistentAgentGraphException(
                    "persistent_agent_edge_endpoint_missing",
                    "An agent graph edge endpoint does not exist.");
            }

            var edges = current.Edges.Select(Snapshot).ToList();
            edges.Add(admitted);
            var next = new PersistentAgentGraphState
            {
                Nodes = SnapshotNodes(current.Nodes),
                Edges = new ReadOnlyCollection<PersistentAgentEdge>(
                    edges.OrderBy(item => item.EdgeId, StringComparer.Ordinal)
                        .Select(Snapshot).ToArray()),
                Revision = checked(current.Revision + 1)
            };
            if (await _store.TryPutAsync(next, current.Revision, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Snapshot(admitted);
            }
        }

        throw Contention();
    }

    public async ValueTask<IReadOnlyList<PersistentAgentNode>> ListAsync(
        string? worldId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 || maximumCount > _options.MaxAgents)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ReadOnlyCollection<PersistentAgentNode>(
            state.Nodes
                .Where(item => worldId is null || item.WorldId == worldId)
                .OrderBy(item => item.AgentId, StringComparer.Ordinal)
                .Take(maximumCount)
                .Select(Snapshot)
                .ToArray());
    }

    private async ValueTask<PersistentAgentNode> MutateNodeAsync(
        string agentId,
        PersistentAgentNode? create,
        Func<PersistentAgentNode, PersistentAgentNode> mutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MaxCommitRetries; attempt++)
        {
            var current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var nodes = current.Nodes.ToDictionary(
                item => item.AgentId,
                Snapshot,
                StringComparer.Ordinal);
            if (!nodes.TryGetValue(agentId, out var node))
            {
                if (create is null)
                {
                    throw new PersistentAgentGraphException(
                        "persistent_agent_missing",
                        "The persistent agent identity does not exist.",
                        agentId);
                }

                if (nodes.Count >= _options.MaxAgents)
                {
                    throw new PersistentAgentGraphException(
                        "persistent_agent_capacity",
                        "The persistent agent identity store is full.");
                }

                node = Snapshot(create);
                node.Revision = Math.Max(1, node.Revision);
                nodes.Add(agentId, node);
            }
            else
            {
                node = mutation(node);
                nodes[agentId] = node;
            }

            var next = new PersistentAgentGraphState
            {
                Nodes = new ReadOnlyCollection<PersistentAgentNode>(
                    nodes.Values.OrderBy(item => item.AgentId, StringComparer.Ordinal)
                        .Select(Snapshot).ToArray()),
                Edges = new ReadOnlyCollection<PersistentAgentEdge>(
                    current.Edges.Select(Snapshot).ToArray()),
                Revision = checked(current.Revision + 1)
            };
            if (await _store.TryPutAsync(next, current.Revision, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Snapshot(node);
            }
        }

        throw Contention();
    }

    private PersistentAgentNode SnapshotAndValidate(PersistentAgentNode node)
    {
        if (node is null || node.Mailbox is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (!PersistentAgentStates.IsKnown(node.State)
            || !AgentContextInheritancePolicies.IsKnown(
                node.ContextInheritancePolicy)
            || node.LastAccessOrderingKey < 0
            || node.Mailbox.Count > _options.MaxMailboxMessagesPerAgent)
        {
            throw new ArgumentException("The persistent agent node is invalid.", nameof(node));
        }

        var mailbox = node.Mailbox.Select(SnapshotAndValidate).ToArray();
        var currentIds = new HashSet<string>(
            mailbox.Select(message => message.MessageId),
            StringComparer.Ordinal);
        if (currentIds.Count != mailbox.Length)
        {
            throw new ArgumentException("Mailbox message identities are duplicated.", nameof(node));
        }

        var acceptedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in mailbox)
        {
            foreach (var acceptance in message.AcceptedMessages)
            {
                if (currentIds.Contains(acceptance.MessageId)
                    || !acceptedIds.Add(acceptance.MessageId))
                {
                    throw new ArgumentException(
                        "Mailbox accepted message identities are duplicated.",
                        nameof(node));
                }
            }
        }

        if (mailbox.Sum(message => 1 + message.AcceptedMessages.Count)
            > _options.MaxMailboxMessagesPerAgent)
        {
            throw new ArgumentException("The persistent agent mailbox is invalid.", nameof(node));
        }

        return new PersistentAgentNode
        {
            AgentId = Required(node.AgentId, nameof(node.AgentId), 128),
            WorldId = Required(node.WorldId, nameof(node.WorldId), 128),
            HistoryId = Required(node.HistoryId, nameof(node.HistoryId), 256),
            ParentAgentId = node.ParentAgentId is null
                ? null
                : Required(node.ParentAgentId, nameof(node.ParentAgentId), 128),
            ContextInheritancePolicy = node.ContextInheritancePolicy,
            State = node.State,
            OwnsUnsettledSideEffect = node.OwnsUnsettledSideEffect,
            LastAccessOrderingKey = node.LastAccessOrderingKey,
            Mailbox = SnapshotMailbox(mailbox),
            Revision = node.Revision
        };
    }

    private AgentMailboxMessage SnapshotAndValidate(AgentMailboxMessage message)
    {
        if (message is null
            || message.OrderingKey < 0
            || message.Revision < 0
            || message.State != AgentMailboxMessageStates.Pending
               && message.State != AgentMailboxMessageStates.Delivered)
        {
            throw new ArgumentException("The mailbox message is invalid.", nameof(message));
        }

        JsonValueInspector.ValidateAndMeasure(
            message.Payload,
            _payloadLimits,
            nameof(message.Payload));
        var accepted = (message.AcceptedMessages ?? Array.Empty<AgentMailboxAcceptance>())
            .Take(_options.MaxMailboxMessagesPerAgent + 1)
            .Select(SnapshotAndValidate)
            .OrderBy(item => item.MessageId, StringComparer.Ordinal)
            .ToArray();
        if (accepted.Length > _options.MaxMailboxMessagesPerAgent
            || accepted.Select(item => item.MessageId)
                .Distinct(StringComparer.Ordinal).Count() != accepted.Length
            || accepted.Any(item => item.MessageId == message.MessageId))
        {
            throw new ArgumentException("The mailbox acceptance ledger is invalid.", nameof(message));
        }

        var result = new AgentMailboxMessage
        {
            MessageId = Required(message.MessageId, nameof(message.MessageId), 128),
            Kind = Required(message.Kind, nameof(message.Kind), 128),
            SenderAgentId = message.SenderAgentId is null
                ? null
                : Required(message.SenderAgentId, nameof(message.SenderAgentId), 128),
            CoalesceKey = message.CoalesceKey is null
                ? null
                : Required(message.CoalesceKey, nameof(message.CoalesceKey), 256),
            AcceptedMessages = new ReadOnlyCollection<AgentMailboxAcceptance>(accepted),
            OrderingKey = message.OrderingKey,
            Payload = message.Payload.Clone(),
            State = message.State,
            Revision = Math.Max(1, message.Revision)
        };
        var currentDigest = MessageDigest(result);
        result.OriginalDigest = string.IsNullOrEmpty(message.OriginalDigest)
            ? currentDigest
            : RequiredDigest(message.OriginalDigest, nameof(message.OriginalDigest));
        if (accepted.Length == 0 && result.OriginalDigest != currentDigest)
        {
            throw new ArgumentException(
                "An uncoalesced mailbox message has an invalid original digest.",
                nameof(message));
        }

        return result;
    }

    private static AgentMailboxAcceptance SnapshotAndValidate(
        AgentMailboxAcceptance acceptance)
    {
        if (acceptance is null)
        {
            throw new ArgumentNullException(nameof(acceptance));
        }

        return new AgentMailboxAcceptance
        {
            MessageId = Required(acceptance.MessageId, nameof(acceptance.MessageId), 128),
            MessageDigest = RequiredDigest(
                acceptance.MessageDigest,
                nameof(acceptance.MessageDigest))
        };
    }

    private static PersistentAgentEdge SnapshotAndValidate(PersistentAgentEdge edge)
    {
        if (edge is null || !PersistentAgentEdgeKinds.IsKnown(edge.Kind))
        {
            throw new ArgumentException("The persistent agent edge is invalid.", nameof(edge));
        }

        return new PersistentAgentEdge
        {
            EdgeId = Required(edge.EdgeId, nameof(edge.EdgeId), 256),
            Kind = edge.Kind,
            FromAgentId = Required(edge.FromAgentId, nameof(edge.FromAgentId), 128),
            ToId = Required(edge.ToId, nameof(edge.ToId), 256),
            Open = edge.Open,
            Revision = Math.Max(1, edge.Revision)
        };
    }

    internal static PersistentAgentGraphState Snapshot(
        PersistentAgentGraphState state) => new()
        {
            Nodes = SnapshotNodes(state.Nodes),
            Edges = new ReadOnlyCollection<PersistentAgentEdge>(
            state.Edges.Select(Snapshot).ToArray()),
            Revision = state.Revision
        };

    internal static PersistentAgentNode Snapshot(PersistentAgentNode node) => new()
    {
        AgentId = node.AgentId,
        WorldId = node.WorldId,
        HistoryId = node.HistoryId,
        ParentAgentId = node.ParentAgentId,
        ContextInheritancePolicy = node.ContextInheritancePolicy,
        State = node.State,
        OwnsUnsettledSideEffect = node.OwnsUnsettledSideEffect,
        LastAccessOrderingKey = node.LastAccessOrderingKey,
        Mailbox = SnapshotMailbox(node.Mailbox),
        Revision = node.Revision
    };

    internal static PersistentAgentEdge Snapshot(PersistentAgentEdge edge) => new()
    {
        EdgeId = edge.EdgeId,
        Kind = edge.Kind,
        FromAgentId = edge.FromAgentId,
        ToId = edge.ToId,
        Open = edge.Open,
        Revision = edge.Revision
    };

    internal static AgentMailboxMessage Snapshot(AgentMailboxMessage message) => new()
    {
        MessageId = message.MessageId,
        Kind = message.Kind,
        SenderAgentId = message.SenderAgentId,
        CoalesceKey = message.CoalesceKey,
        OriginalDigest = message.OriginalDigest,
        AcceptedMessages = new ReadOnlyCollection<AgentMailboxAcceptance>(
            message.AcceptedMessages.Select(Snapshot).ToArray()),
        OrderingKey = message.OrderingKey,
        Payload = message.Payload.Clone(),
        State = message.State,
        Revision = message.Revision
    };

    private static AgentMailboxAcceptance Snapshot(AgentMailboxAcceptance acceptance) => new()
    {
        MessageId = acceptance.MessageId,
        MessageDigest = acceptance.MessageDigest
    };

    private static IReadOnlyList<PersistentAgentNode> SnapshotNodes(
        IEnumerable<PersistentAgentNode> nodes) =>
        new ReadOnlyCollection<PersistentAgentNode>(nodes.Select(Snapshot).ToArray());

    private static IReadOnlyList<AgentMailboxMessage> SnapshotMailbox(
        IEnumerable<AgentMailboxMessage> messages) =>
        new ReadOnlyCollection<AgentMailboxMessage>(
            messages.OrderBy(item => item.OrderingKey)
                .ThenBy(item => item.MessageId, StringComparer.Ordinal)
                .Select(Snapshot).ToArray());

    private static bool IdentityEquivalent(
        PersistentAgentNode left,
        PersistentAgentNode right) =>
        left.AgentId == right.AgentId
        && left.WorldId == right.WorldId
        && left.HistoryId == right.HistoryId
        && left.ParentAgentId == right.ParentAgentId
        && left.ContextInheritancePolicy == right.ContextInheritancePolicy;

    private static bool EdgeEquivalent(
        PersistentAgentEdge left,
        PersistentAgentEdge right) =>
        left.EdgeId == right.EdgeId
        && left.Kind == right.Kind
        && left.FromAgentId == right.FromAgentId
        && left.ToId == right.ToId;

    private static bool CanTransition(string from, string to)
    {
        if (from == to)
        {
            return true;
        }

        if (PersistentAgentStates.IsTerminal(from))
        {
            return false;
        }

        return to switch
        {
            PersistentAgentStates.Running =>
                from is PersistentAgentStates.Open
                    or PersistentAgentStates.Idle
                    or PersistentAgentStates.Waiting
                    or PersistentAgentStates.Evicted,
            PersistentAgentStates.Idle => from == PersistentAgentStates.Running,
            PersistentAgentStates.Waiting =>
                from is PersistentAgentStates.Running or PersistentAgentStates.Idle,
            PersistentAgentStates.Evicted =>
                from is PersistentAgentStates.Idle or PersistentAgentStates.Waiting,
            PersistentAgentStates.Closed or PersistentAgentStates.Failed => true,
            _ => false
        };
    }

    private static string MessageDigest(AgentMailboxMessage message) =>
        CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("messageId", JsonArrayBuilder.String(message.MessageId)),
            ("kind", JsonArrayBuilder.String(message.Kind)),
            ("sender", JsonArrayBuilder.String(message.SenderAgentId ?? string.Empty)),
            ("coalesce", JsonArrayBuilder.String(message.CoalesceKey ?? string.Empty)),
            ("ordering", JsonArrayBuilder.Number(message.OrderingKey)),
            ("payload", message.Payload.Clone())));

    private void EnsureMailboxCapacity(
        IEnumerable<AgentMailboxMessage> mailbox,
        string agentId)
    {
        if (mailbox.Sum(message => 1 + message.AcceptedMessages.Count)
            >= _options.MaxMailboxMessagesPerAgent)
        {
            throw new PersistentAgentGraphException(
                "agent_mailbox_capacity",
                "The persistent agent mailbox is full.",
                agentId);
        }
    }

    private static string RequiredDigest(string value, string name)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", name);
        }

        return value;
    }

    private static PersistentAgentGraphException Contention() =>
        new(
            "persistent_agent_graph_contention",
            "The persistent agent graph changed too often to commit the operation.");

    private static string Required(string value, string name, int maximum) =>
        RuntimeGuard.RequiredUtf8(value, maximum, name);
}

public sealed class InMemoryPersistentAgentGraphStore : IPersistentAgentGraphStore
{
    private readonly object _gate = new();
    private PersistentAgentGraphState _state = new();

    public ValueTask<PersistentAgentGraphState> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new ValueTask<PersistentAgentGraphState>(
                PersistentAgentGraph.Snapshot(_state));
        }
    }

    public ValueTask<bool> TryPutAsync(
        PersistentAgentGraphState state,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state.Revision != expectedRevision)
            {
                return new ValueTask<bool>(false);
            }

            _state = PersistentAgentGraph.Snapshot(state);
            return new ValueTask<bool>(true);
        }
    }
}

public interface IResidentAgentRuntime : IAsyncDisposable
{
    string AgentId { get; }

    bool OwnsUnsettledSideEffect { get; }
}

public interface IResidentAgentRuntimeLoader
{
    ValueTask<IResidentAgentRuntime> LoadAsync(
        PersistentAgentNode node,
        CancellationToken cancellationToken);
}

public sealed class AgentResidencyOptions
{
    public int MaxResidentInstances { get; set; } = 32;

    public int MaxConcurrentExecutions { get; set; } = 8;

    public int MaxConcurrentModelCalls { get; set; } = 8;

    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan UnloadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (MaxResidentInstances is < 1 or > 65_536
            || MaxConcurrentExecutions is < 1 or > 4_096
            || MaxConcurrentModelCalls is < 1 or > 4_096
            || LoadTimeout < TimeSpan.FromMilliseconds(10)
            || LoadTimeout > TimeSpan.FromMinutes(5)
            || UnloadTimeout < TimeSpan.FromMilliseconds(10)
            || UnloadTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(AgentResidencyOptions));
        }
    }
}

public sealed class AgentResidencyManager : IAsyncDisposable
{
    private readonly PersistentAgentGraph _graph;
    private readonly IResidentAgentRuntimeLoader _loader;
    private readonly AgentResidencyOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _executionSlots;
    private readonly SemaphoreSlim _modelCallSlots;
    private readonly Dictionary<string, ResidentEntry> _residents =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _unloading =
        new(StringComparer.Ordinal);
    private int _residentCount;
    private int _closed;

    public AgentResidencyManager(
        PersistentAgentGraph graph,
        IResidentAgentRuntimeLoader loader,
        AgentResidencyOptions? options = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _options = options ?? new AgentResidencyOptions();
        _options.Validate();
        _executionSlots = new SemaphoreSlim(
            _options.MaxConcurrentExecutions,
            _options.MaxConcurrentExecutions);
        _modelCallSlots = new SemaphoreSlim(
            _options.MaxConcurrentModelCalls,
            _options.MaxConcurrentModelCalls);
    }

    public int ResidentCount => Volatile.Read(ref _residentCount);

    public ValueTask<AgentCapacityLease> AcquireExecutionAsync(
        CancellationToken cancellationToken = default) =>
        AcquireCapacityAsync(_executionSlots, cancellationToken);

    public ValueTask<AgentCapacityLease> AcquireModelCallAsync(
        CancellationToken cancellationToken = default) =>
        AcquireCapacityAsync(_modelCallSlots, cancellationToken);

    public async ValueTask<AgentResidencyLease> AcquireAsync(
        string agentId,
        long orderingKey,
        CancellationToken cancellationToken = default)
    {
        agentId = RuntimeGuard.RequiredUtf8(agentId, 128, nameof(agentId));
        if (orderingKey < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderingKey));
        }

        while (true)
        {
            ThrowIfClosed();
            ResidentEntry? entry = null;
            ResidentEntry? victim = null;
            Task? waitForUnload = null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfClosed();
                if (_unloading.TryGetValue(agentId, out var unloading))
                {
                    waitForUnload = unloading.Task;
                }
                else if (_residents.TryGetValue(agentId, out entry))
                {
                    entry.Owners++;
                    entry.LastAccessOrderingKey = Math.Max(
                        entry.LastAccessOrderingKey,
                        orderingKey);
                }
                else if (_residents.Count + _unloading.Count
                         < _options.MaxResidentInstances)
                {
                    var node = await _graph.TryGetAsync(agentId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new PersistentAgentGraphException(
                            "persistent_agent_missing",
                            "The persistent agent identity does not exist.",
                            agentId);
                    if (PersistentAgentStates.IsTerminal(node.State))
                    {
                        throw new PersistentAgentGraphException(
                            "persistent_agent_terminal",
                            "A terminal persistent agent cannot become resident.",
                            agentId);
                    }

                    entry = new ResidentEntry(
                        agentId,
                        orderingKey,
                        LoadAsync(node));
                    _residents.Add(agentId, entry);
                    Interlocked.Increment(ref _residentCount);
                }
                else
                {
                    victim = _residents.Values
                        .Where(candidate =>
                            candidate.Owners == 0
                            && candidate.LoadTask.IsCompletedSuccessfully
                            && !candidate.LoadTask.Result.OwnsUnsettledSideEffect)
                        .OrderBy(candidate => candidate.LastAccessOrderingKey)
                        .ThenBy(candidate => candidate.AgentId, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (victim is null)
                    {
                        throw new PersistentAgentGraphException(
                            "agent_residency_capacity",
                            "All resident agent instances are busy or own unsettled side effects.",
                            agentId);
                    }

                    _residents.Remove(victim.AgentId);
                    _unloading.Add(
                        victim.AgentId,
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously));
                }
            }
            finally
            {
                _gate.Release();
            }

            if (waitForUnload is not null)
            {
                await AwaitWithCancellationAsync(waitForUnload, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (victim is not null)
            {
                try
                {
                    await UnloadAsync(victim, cancellationToken)
                        .ConfigureAwait(false);
                    await CompleteUnloadReservationAsync(
                            victim,
                            readd: false,
                            releaseWaiters: true)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await CompleteUnloadReservationAsync(
                            victim,
                            readd: !victim.UnloadStarted,
                            releaseWaiters: !victim.UnloadStarted
                                || victim.RuntimeDisposed)
                        .ConfigureAwait(false);
                    if (victim.UnloadStarted && !victim.RuntimeDisposed)
                    {
                        ScheduleLateUnloadFinalization(victim);
                    }

                    throw;
                }

                continue;
            }

            try
            {
                var runtime = await AwaitWithTimeoutAsync(
                        entry!.LoadTask,
                        _options.LoadTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _graph.TransitionAsync(
                        agentId,
                        PersistentAgentStates.Running,
                        orderingKey,
                        runtime.OwnsUnsettledSideEffect,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AgentResidencyLease(this, entry, runtime);
            }
            catch
            {
                await ReleaseFailedAcquireAsync(entry!).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<IResidentAgentRuntime> LoadAsync(PersistentAgentNode node)
    {
        using var timeout = new CancellationTokenSource(_options.LoadTimeout);
        var runtime = await _loader.LoadAsync(node, timeout.Token).ConfigureAwait(false);
        if (runtime is null || runtime.AgentId != node.AgentId)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            throw new PersistentAgentGraphException(
                "agent_residency_identity_mismatch",
                "The loaded runtime does not match the persistent agent identity.",
                node.AgentId);
        }

        return runtime;
    }

    private async ValueTask ReleaseAsync(
        ResidentEntry entry,
        IResidentAgentRuntime runtime,
        string finalState,
        long orderingKey)
    {
        var becameIdle = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.Owners <= 0)
            {
                throw new InvalidOperationException("Agent residency owner count underflowed.");
            }

            entry.Owners--;
            becameIdle = entry.Owners == 0;
            entry.LastAccessOrderingKey = Math.Max(
                entry.LastAccessOrderingKey,
                orderingKey);
        }
        finally
        {
            _gate.Release();
        }

        if (becameIdle)
        {
            await _graph.TransitionAsync(
                    entry.AgentId,
                    finalState,
                    entry.LastAccessOrderingKey,
                    runtime.OwnsUnsettledSideEffect)
                .ConfigureAwait(false);
        }
    }

    private async Task ReleaseFailedAcquireAsync(ResidentEntry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.Owners > 0)
            {
                entry.Owners--;
            }

            if (entry.Owners == 0
                && entry.LoadTask.IsCompleted
                && !entry.LoadTask.IsCompletedSuccessfully)
            {
                if (_residents.Remove(entry.AgentId))
                {
                    Interlocked.Decrement(ref _residentCount);
                }
            }
            else if (entry.Owners == 0 && !entry.LoadTask.IsCompleted)
            {
                ScheduleLateLoadFailureCleanup(entry);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ScheduleLateLoadFailureCleanup(ResidentEntry entry)
    {
        if (Interlocked.Exchange(ref entry.LateLoadFinalizerScheduled, 1) != 0)
        {
            return;
        }

        _ = FinalizeLateLoadFailureAsync(entry);
    }

    private async Task FinalizeLateLoadFailureAsync(ResidentEntry entry)
    {
        try
        {
            await entry.LoadTask.ConfigureAwait(false);
            return;
        }
        catch
        {
            // A failed or cancelled late load never produced an owned runtime.
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (entry.Owners == 0
                && _residents.TryGetValue(entry.AgentId, out var current)
                && ReferenceEquals(current, entry)
                && _residents.Remove(entry.AgentId))
            {
                Interlocked.Decrement(ref _residentCount);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UnloadAsync(
        ResidentEntry entry,
        CancellationToken cancellationToken)
    {
        var runtime = await entry.LoadTask.ConfigureAwait(false);
        if (runtime.OwnsUnsettledSideEffect)
        {
            throw new PersistentAgentGraphException(
                "persistent_agent_side_effect_owned",
                "An agent acquired a side effect while eviction was being prepared.",
                entry.AgentId);
        }

        entry.UnloadStarted = true;
        var dispose = runtime.DisposeAsync().AsTask();
        entry.UnloadTask = dispose;
        await AwaitWithTimeoutAsync(
                dispose,
                _options.UnloadTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        entry.RuntimeDisposed = true;
        await PersistEvictedAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistEvictedAsync(
        ResidentEntry entry,
        CancellationToken cancellationToken)
    {
        var durable = await _graph.TryGetAsync(entry.AgentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PersistentAgentGraphException(
                "persistent_agent_missing",
                "The persistent agent identity disappeared before eviction.",
                entry.AgentId);
        await _graph.TransitionAsync(
                entry.AgentId,
                durable.State,
                entry.LastAccessOrderingKey,
                ownsUnsettledSideEffect: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _graph.TransitionAsync(
                entry.AgentId,
                PersistentAgentStates.Evicted,
                entry.LastAccessOrderingKey,
                ownsUnsettledSideEffect: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void ScheduleLateUnloadFinalization(ResidentEntry entry)
    {
        if (Interlocked.Exchange(ref entry.LateFinalizerScheduled, 1) != 0)
        {
            return;
        }

        _ = FinalizeLateUnloadAsync(entry);
    }

    private async Task FinalizeLateUnloadAsync(ResidentEntry entry)
    {
        try
        {
            var unload = entry.UnloadTask;
            if (unload is null)
            {
                return;
            }

            await unload.ConfigureAwait(false);
            entry.RuntimeDisposed = true;
            try
            {
                await PersistEvictedAsync(entry, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The runtime is already disposed. A later load will reconcile
                // the durable lifecycle state through the normal transition path.
            }

            await CompleteUnloadReservationAsync(
                    entry,
                    readd: false,
                    releaseWaiters: true)
                .ConfigureAwait(false);
        }
        catch
        {
            // A failed DisposeAsync leaves runtime ownership uncertain. Keep the
            // reservation quarantined so a second live instance cannot be loaded.
        }
    }

    private static async ValueTask<AgentCapacityLease> AcquireCapacityAsync(
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AgentCapacityLease(slots);
    }

    private static async Task<T> AwaitWithTimeoutAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var delay = Task.Delay(timeout, deadline.Token);
        var completed = await Task.WhenAny(operation, delay).ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveLateFault(operation);
            throw new TimeoutException("The resident agent operation timed out.");
        }

        deadline.Cancel();
        return await operation.ConfigureAwait(false);
    }

    private static async Task AwaitWithTimeoutAsync(
        Task operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var delay = Task.Delay(timeout, deadline.Token);
        var completed = await Task.WhenAny(operation, delay).ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveLateFault(operation);
            throw new TimeoutException("The resident agent operation timed out.");
        }

        deadline.Cancel();
        await operation.ConfigureAwait(false);
    }

    private static async Task AwaitWithCancellationAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompleted)
        {
            await operation.ConfigureAwait(false);
            return;
        }

        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(operation, cancelled).ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        await operation.ConfigureAwait(false);
    }

    private async Task CompleteUnloadReservationAsync(
        ResidentEntry entry,
        bool readd,
        bool releaseWaiters)
    {
        TaskCompletionSource<bool>? signal = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_unloading.TryGetValue(entry.AgentId, out signal))
            {
                return;
            }

            if (readd)
            {
                _unloading.Remove(entry.AgentId);
                _residents.Add(entry.AgentId, entry);
            }
            else if (releaseWaiters)
            {
                _unloading.Remove(entry.AgentId);
                Interlocked.Decrement(ref _residentCount);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (releaseWaiters)
        {
            signal?.TrySetResult(true);
        }
    }

    private static void ObserveLateFault(Task operation)
    {
        _ = operation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        ResidentEntry[] residents;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed != 0)
            {
                return;
            }

            if (_residents.Values.Any(entry => entry.Owners != 0))
            {
                throw new InvalidOperationException(
                    "Cannot dispose agent residency while leases are active.");
            }

            if (_unloading.Count != 0)
            {
                throw new InvalidOperationException(
                    "Cannot dispose agent residency while runtimes are unloading or quarantined.");
            }

            if (_residents.Values.Any(entry =>
                    !entry.LoadTask.IsCompletedSuccessfully
                    || entry.LoadTask.Result.OwnsUnsettledSideEffect))
            {
                throw new InvalidOperationException(
                    "Cannot dispose agent residency while runtimes are loading or own unsettled side effects.");
            }

            Volatile.Write(ref _closed, 1);
            residents = _residents.Values.ToArray();
            _residents.Clear();
            Volatile.Write(ref _residentCount, 0);
        }
        finally
        {
            _gate.Release();
        }

        var failures = new List<Exception>();
        foreach (var resident in residents)
        {
            try
            {
                await UnloadAsync(resident, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        _executionSlots.Dispose();
        _modelCallSlots.Dispose();
        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more resident agent runtimes could not be unloaded.",
                failures);
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentResidencyManager));
        }
    }

    internal sealed class ResidentEntry
    {
        public ResidentEntry(
            string agentId,
            long lastAccessOrderingKey,
            Task<IResidentAgentRuntime> loadTask)
        {
            AgentId = agentId;
            LastAccessOrderingKey = lastAccessOrderingKey;
            LoadTask = loadTask;
            Owners = 1;
        }

        public string AgentId { get; }

        public Task<IResidentAgentRuntime> LoadTask { get; }

        public int Owners { get; set; }

        public long LastAccessOrderingKey { get; set; }

        public bool UnloadStarted { get; set; }

        public bool RuntimeDisposed { get; set; }

        public Task? UnloadTask { get; set; }

        public int LateLoadFinalizerScheduled;

        public int LateFinalizerScheduled;
    }

    public sealed class AgentResidencyLease : IAsyncDisposable
    {
        private AgentResidencyManager? _owner;
        private ResidentEntry? _entry;
        private long _orderingKey;

        internal AgentResidencyLease(
            AgentResidencyManager owner,
            ResidentEntry entry,
            IResidentAgentRuntime runtime)
        {
            _owner = owner;
            _entry = entry;
            Runtime = runtime;
            _orderingKey = entry.LastAccessOrderingKey;
        }

        public IResidentAgentRuntime Runtime { get; }

        public string FinalState { get; set; } = PersistentAgentStates.Idle;

        public void Touch(long orderingKey)
        {
            if (orderingKey < _orderingKey)
            {
                throw new ArgumentOutOfRangeException(nameof(orderingKey));
            }

            _orderingKey = orderingKey;
        }

        public async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var entry = Interlocked.Exchange(ref _entry, null);
            if (owner is not null && entry is not null)
            {
                await owner.ReleaseAsync(entry, Runtime, FinalState, _orderingKey)
                    .ConfigureAwait(false);
            }
        }
    }

    public sealed class AgentCapacityLease : IDisposable
    {
        private SemaphoreSlim? _slots;

        internal AgentCapacityLease(SemaphoreSlim slots)
        {
            _slots = slots;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _slots, null)?.Release();
        }
    }
}
