using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenGameAgent.Protocol;

public static class RuntimeProtocolJson
{
    public static JsonSerializerOptions CreateSerializerOptions() => new(Converter.Settings)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
