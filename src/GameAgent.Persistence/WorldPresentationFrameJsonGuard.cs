using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence;

/// <summary>
/// Allocation-free first pass over an untrusted frame. DTO materialization is
/// allowed only after structural and collection limits have been proven.
/// </summary>
internal static class WorldPresentationFrameJsonGuard
{
    public static void Validate(
        ReadOnlySpan<byte> payload,
        WorldPresentationLimits limits,
        int maxTokens)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (maxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }

        var reader = new Utf8JsonReader(
            payload,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = checked(limits.MaxJsonDepth + 12)
            });
        var stack = new List<ContainerState>(
            checked(limits.MaxJsonDepth + 12));
        var tokens = 0;
        var generalContainerLimit = Math.Max(
            limits.MaxJsonNodes,
            Math.Max(
                limits.MaxAudienceMembers,
                Math.Max(
                    limits.MaxMediaCues,
                    limits.MaxParentPresentationIds)));
        while (reader.Read())
        {
            tokens = checked(tokens + 1);
            if (tokens > maxTokens)
            {
                throw Capacity(
                    nameof(
                        FileWorldPresentationStoreOptions
                            .MaxFrameJsonTokens),
                    maxTokens,
                    tokens);
            }

            var maxRawStringBytes = checked(
                Math.Max(
                    limits.MaxPayloadUtf8Bytes,
                    limits.MaxMetadataUtf8Bytes)
                * 6);
            if (reader.TokenType == JsonTokenType.PropertyName
                && reader.ValueSpan.Length > maxRawStringBytes)
            {
                throw Capacity(
                    "raw JSON property bytes",
                    maxRawStringBytes,
                    reader.ValueSpan.Length);
            }

            if (reader.TokenType == JsonTokenType.String
                && reader.ValueSpan.Length > maxRawStringBytes)
            {
                throw Capacity(
                    "raw JSON string bytes",
                    maxRawStringBytes,
                    reader.ValueSpan.Length);
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (stack.Count == 0 || stack[^1].IsArray)
                    {
                        throw new JsonException(
                            "A property name must belong to an object.");
                    }

                    var propertyContainer = stack[^1];
                    Increment(
                        ref propertyContainer,
                        generalContainerLimit,
                        "JSON object property count");
                    var knownProperty = ClassifyProperty(ref reader);
                    RejectDuplicateStructuralProperty(
                        ref propertyContainer,
                        knownProperty);
                    propertyContainer.PendingProperty = knownProperty;
                    stack[^1] = propertyContainer;
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    var path = FramePath.Root;
                    if (stack.Count > 0)
                    {
                        var parent = stack[^1];
                        if (parent.IsArray)
                        {
                            IncrementArray(
                                ref parent,
                                limits,
                                generalContainerLimit);
                        }

                        path = ResolvePath(
                            parent.Path,
                            parent.PendingProperty);
                        parent.PendingProperty =
                            KnownProperty.None;
                        stack[^1] = parent;
                    }

                    stack.Add(
                        new ContainerState(
                            reader.TokenType
                            == JsonTokenType.StartArray,
                            path));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (stack.Count == 0)
                    {
                        throw new JsonException(
                            "The JSON container stack is unbalanced.");
                    }

                    stack.RemoveAt(stack.Count - 1);
                    break;

                default:
                    if (stack.Count > 0)
                    {
                        var valueContainer = stack[^1];
                        if (valueContainer.IsArray)
                        {
                            IncrementArray(
                                ref valueContainer,
                                limits,
                                generalContainerLimit);
                        }
                        else
                        {
                            valueContainer.PendingProperty =
                                KnownProperty.None;
                        }

                        stack[^1] = valueContainer;
                    }

                    break;
            }
        }

        if (stack.Count != 0)
        {
            throw new JsonException(
                "The JSON container stack is incomplete.");
        }
    }

    private static void IncrementArray(
        ref ContainerState container,
        WorldPresentationLimits limits,
        int generalLimit)
    {
        var limit = container.Path switch
        {
            FramePath.AudienceMembers => limits.MaxAudienceMembers,
            FramePath.MediaCues => limits.MaxMediaCues,
            FramePath.ParentPresentationIds =>
                limits.MaxParentPresentationIds,
            _ => generalLimit
        };
        var label = container.Path switch
        {
            FramePath.AudienceMembers => "audience members",
            FramePath.MediaCues => "media cues",
            FramePath.ParentPresentationIds =>
                "parent presentation IDs",
            _ => "JSON array item count"
        };
        Increment(ref container, limit, label);
    }

    private static void Increment(
        ref ContainerState container,
        int limit,
        string label)
    {
        container.DirectItems = checked(container.DirectItems + 1);
        if (container.DirectItems > limit)
        {
            throw Capacity(label, limit, container.DirectItems);
        }
    }

    private static KnownProperty ClassifyProperty(
        ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("presentation"))
        {
            return KnownProperty.Presentation;
        }

        if (reader.ValueTextEquals("audience"))
        {
            return KnownProperty.Audience;
        }

        if (reader.ValueTextEquals("content"))
        {
            return KnownProperty.Content;
        }

        if (reader.ValueTextEquals("provenance"))
        {
            return KnownProperty.Provenance;
        }

        if (reader.ValueTextEquals("members"))
        {
            return KnownProperty.Members;
        }

        if (reader.ValueTextEquals("mediaCues"))
        {
            return KnownProperty.MediaCues;
        }

        if (reader.ValueTextEquals("parentPresentationIds"))
        {
            return KnownProperty.ParentPresentationIds;
        }

        return KnownProperty.Other;
    }

    private static FramePath ResolvePath(
        FramePath parent,
        KnownProperty property)
    {
        return (parent, property) switch
        {
            (FramePath.Root, KnownProperty.Presentation) =>
                FramePath.Presentation,
            (FramePath.Presentation, KnownProperty.Audience) =>
                FramePath.Audience,
            (FramePath.Presentation, KnownProperty.Content) =>
                FramePath.Content,
            (FramePath.Presentation, KnownProperty.Provenance) =>
                FramePath.Provenance,
            (FramePath.Audience, KnownProperty.Members) =>
                FramePath.AudienceMembers,
            (FramePath.Content, KnownProperty.MediaCues) =>
                FramePath.MediaCues,
            (FramePath.Provenance, KnownProperty.ParentPresentationIds) =>
                FramePath.ParentPresentationIds,
            _ => FramePath.Other
        };
    }

    private static void RejectDuplicateStructuralProperty(
        ref ContainerState container,
        KnownProperty property)
    {
        var structural = (container.Path, property) switch
        {
            (FramePath.Root, KnownProperty.Presentation) => true,
            (FramePath.Presentation, KnownProperty.Audience) => true,
            (FramePath.Presentation, KnownProperty.Content) => true,
            (FramePath.Presentation, KnownProperty.Provenance) => true,
            (FramePath.Audience, KnownProperty.Members) => true,
            (FramePath.Content, KnownProperty.MediaCues) => true,
            (FramePath.Provenance,
                KnownProperty.ParentPresentationIds) => true,
            _ => false
        };
        if (!structural)
        {
            return;
        }

        var bit = 1u << (int)property;
        if ((container.SeenStructuralProperties & bit) != 0)
        {
            throw new JsonException(
                "A structural presentation property is duplicated.");
        }

        container.SeenStructuralProperties |= bit;
    }

    private static FileWorldPresentationStoreCapacityException Capacity(
        string name,
        long limit,
        long attempted)
    {
        return new FileWorldPresentationStoreCapacityException(
            name,
            limit,
            attempted);
    }

    private enum KnownProperty
    {
        None,
        Other,
        Presentation,
        Audience,
        Content,
        Provenance,
        Members,
        MediaCues,
        ParentPresentationIds
    }

    private enum FramePath
    {
        Root,
        Other,
        Presentation,
        Audience,
        Content,
        Provenance,
        AudienceMembers,
        MediaCues,
        ParentPresentationIds
    }

    private struct ContainerState
    {
        public ContainerState(bool isArray, FramePath path)
        {
            IsArray = isArray;
            Path = path;
            DirectItems = 0;
            PendingProperty = KnownProperty.None;
            SeenStructuralProperties = 0;
        }

        public bool IsArray { get; }

        public FramePath Path { get; }

        public int DirectItems { get; set; }

        public KnownProperty PendingProperty { get; set; }

        public uint SeenStructuralProperties { get; set; }
    }
}
