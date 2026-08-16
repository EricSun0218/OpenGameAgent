using OpenGameAgent;
using OpenGameAgent.Attachments;
using OpenGameAgent.Attachments.Local;
using OpenGameAgent.Persistence;
using OpenGameAgent.Providers.OpenAICompatible;
using OpenGameAgent.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("model");
builder.Services.AddSingleton<IGameImageAttachmentStore>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var attachmentDirectory = configuration["OpenGameAgent:AttachmentDirectory"]
        ?? Path.Combine(AppContext.BaseDirectory, "data", "attachments");
    return new FileGameImageAttachmentStore(attachmentDirectory);
});
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
    var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    GameAgentRuntimeOptions runtimeOptions;
    if (configuration.GetSection("OpenGameAgent:ModelRoutes").GetChildren().Any())
    {
        var routing = StockGameAgentModelRouting.Create(configuration, httpClientFactory);
        runtimeOptions = new GameAgentRuntimeOptions(routing.DefaultProvider, routing.DefaultModel)
        {
            ModelSelector = routing.SelectAsync,
        };
    }
    else
    {
        var endpointText = configuration["OpenGameAgent:ModelEndpoint"]
            ?? throw new InvalidOperationException("Configure OpenGameAgent:ModelEndpoint or ModelRoutes.");
        var model = configuration["OpenGameAgent:Model"]
            ?? throw new InvalidOperationException("Configure OpenGameAgent:Model.");
        var providerOptions = new OpenAICompatibleProviderOptions(
            httpClientFactory.CreateClient("model"),
            new Uri(endpointText))
        {
            ApiKey = configuration["OpenGameAgent:ApiKey"],
        };
        runtimeOptions = new GameAgentRuntimeOptions(new OpenAICompatibleProvider(providerOptions), model);
    }

    runtimeOptions.Instructions = configuration["OpenGameAgent:Instructions"] ?? string.Empty;
    runtimeOptions.ImageAttachments = serviceProvider.GetRequiredService<IGameImageAttachmentStore>();
    runtimeOptions.SessionStore = new FileGameSessionStore(
        configuration["OpenGameAgent:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data", "sessions"));
    return new GameAgentRuntime(runtimeOptions);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseOpenGameAgentApiKey(builder.Configuration["OpenGameAgent:ServerApiKey"]);
app.MapOpenGameAgent();
app.Run();

public partial class Program;
