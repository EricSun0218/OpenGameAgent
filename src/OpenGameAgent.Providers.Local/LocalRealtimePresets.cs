using OpenGameAgent.Providers.OpenAI.Realtime;

namespace OpenGameAgent.Providers.Local;

public static class LocalRealtimePresets
{
    public static OpenAIRealtimeTransportOptions LocalAi(Uri? endpoint = null) => new()
    {
        Endpoint = endpoint ?? new Uri("ws://127.0.0.1:8080/v1/realtime"),
        AllowAnonymousLoopback = true,
    };

    public static OpenAIRealtimeTransportOptions Speaches(Uri? endpoint = null) => new()
    {
        Endpoint = endpoint ?? new Uri("ws://127.0.0.1:8000/v1/realtime"),
        AllowAnonymousLoopback = true,
    };
}
