using OpenGameAgent;
using OpenGameAgent.Persistence;
using OpenGameAgent.Providers.OpenAICompatible;
using OpenGameAgent.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("model");
builder.Services.AddSingleton<IGameActionJournal>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var actionDirectory = configuration["OpenGameAgent:ActionDirectory"]
        ?? Path.Combine(AppContext.BaseDirectory, "data", "actions");
    return new FileGameActionJournal(actionDirectory);
});
builder.Services.AddSingleton<GameActionExchange>();
builder.Services.AddSingleton(serviceProvider => new DurableGameActionDispatcher(
    serviceProvider.GetRequiredService<IGameActionJournal>(),
    serviceProvider.GetRequiredService<GameActionExchange>()));
builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var endpointText = configuration["OpenGameAgent:ModelEndpoint"]
        ?? throw new InvalidOperationException("Configure OpenGameAgent:ModelEndpoint.");
    var model = configuration["OpenGameAgent:Model"]
        ?? throw new InvalidOperationException("Configure OpenGameAgent:Model.");
    var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("model");
    var providerOptions = new OpenAICompatibleProviderOptions(httpClient, new Uri(endpointText))
    {
        ApiKey = configuration["OpenGameAgent:ApiKey"],
    };
    var runtimeOptions = new GameAgentRuntimeOptions(new OpenAICompatibleProvider(providerOptions), model)
    {
        Instructions = configuration["OpenGameAgent:Instructions"] ?? string.Empty,
        SessionStore = new FileGameSessionStore(
            configuration["OpenGameAgent:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data", "sessions")),
    };
    return new GameAgentRuntime(runtimeOptions);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseOpenGameAgentApiKey(builder.Configuration["OpenGameAgent:ServerApiKey"]);
app.MapOpenGameAgent();
app.Run();

public partial class Program;
