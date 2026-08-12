using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public sealed class GameInteractionOption
{
    public GameInteractionOption(
        string id,
        string label,
        string description,
        bool recommended = false,
        string? payloadJson = null)
    {
        Id = RequireText(id, nameof(id), 128);
        Label = RequireText(label, nameof(label), 256);
        Description = RequireText(description, nameof(description), 4_096);
        Recommended = recommended;
        PayloadJson = payloadJson is null ? null : RequireJson(payloadJson, nameof(payloadJson));
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public bool Recommended { get; }

    public string? PayloadJson { get; }

    private static string RequireText(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new ArgumentException($"{name} must contain 1 to {maximum} characters.", name);
        }

        return value;
    }

    private static string RequireJson(string value, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The payload must be valid JSON.", name, exception);
        }
    }
}

public sealed class GameInteractionQuestion
{
    public GameInteractionQuestion(
        string id,
        string prompt,
        IEnumerable<GameInteractionOption> options,
        bool multiSelect = false,
        bool allowCustomAnswer = true,
        string? payloadJson = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            throw new ArgumentException("A question ID with at most 128 characters is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 8_192)
        {
            throw new ArgumentException("A question prompt with at most 8192 characters is required.", nameof(prompt));
        }

        var copied = (options ?? throw new ArgumentNullException(nameof(options))).ToArray();
        if (copied.Length < 2 || copied.Length > 8 || copied.Any(value => value is null))
        {
            throw new ArgumentException("A question requires 2 to 8 non-null options.", nameof(options));
        }

        var duplicate = copied.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate option ID '{duplicate.Key}'.", nameof(options));
        }

        if (copied.Count(value => value.Recommended) > 1)
        {
            throw new ArgumentException("A question can recommend at most one option.", nameof(options));
        }

        Id = id;
        Prompt = prompt;
        Options = Array.AsReadOnly(copied);
        MultiSelect = multiSelect;
        AllowCustomAnswer = allowCustomAnswer;
        PayloadJson = payloadJson is null ? null : RequireJson(payloadJson);
    }

    public string Id { get; }

    public string Prompt { get; }

    public IReadOnlyList<GameInteractionOption> Options { get; }

    public bool MultiSelect { get; }

    public bool AllowCustomAnswer { get; }

    public string? PayloadJson { get; }

    private static string RequireJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The question payload must be valid JSON.", nameof(value), exception);
        }
    }
}

public sealed class GameInteractionRequest
{
    public GameInteractionRequest(
        string requestId,
        GameInput input,
        IEnumerable<GameInteractionQuestion> questions)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("A request ID is required.", nameof(requestId));
        }

        RequestId = requestId;
        Input = input ?? throw new ArgumentNullException(nameof(input));
        var copied = (questions ?? throw new ArgumentNullException(nameof(questions))).ToArray();
        if (copied.Length < 1 || copied.Length > 8 || copied.Any(value => value is null))
        {
            throw new ArgumentException("An interaction requires 1 to 8 non-null questions.", nameof(questions));
        }

        var duplicate = copied.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate question ID '{duplicate.Key}'.", nameof(questions));
        }

        Questions = Array.AsReadOnly(copied);
    }

    public string RequestId { get; }

    public GameInput Input { get; }

    public IReadOnlyList<GameInteractionQuestion> Questions { get; }
}

public sealed class GameInteractionAnswer
{
    public GameInteractionAnswer(
        string questionId,
        IEnumerable<string>? selectedOptionIds = null,
        string? customAnswer = null)
    {
        if (string.IsNullOrWhiteSpace(questionId) || questionId.Length > 128)
        {
            throw new ArgumentException("A question ID with at most 128 characters is required.", nameof(questionId));
        }

        if (customAnswer?.Length > 32_768)
        {
            throw new ArgumentException("A custom answer is too large.", nameof(customAnswer));
        }

        QuestionId = questionId;
        var selected = (selectedOptionIds ?? Array.Empty<string>())
            .Select(value => string.IsNullOrWhiteSpace(value) || value.Length > 128
                ? throw new ArgumentException("A selected option ID must contain 1 to 128 characters.", nameof(selectedOptionIds))
                : value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selected.Length > 8)
        {
            throw new ArgumentException("An answer can select at most 8 options.", nameof(selectedOptionIds));
        }

        SelectedOptionIds = Array.AsReadOnly(selected);
        CustomAnswer = customAnswer;
    }

    public string QuestionId { get; }

    public IReadOnlyList<string> SelectedOptionIds { get; }

    public string? CustomAnswer { get; }
}

public sealed class GameInteractionResponse
{
    public GameInteractionResponse(bool cancelled, IEnumerable<GameInteractionAnswer>? answers = null)
    {
        Cancelled = cancelled;
        var copied = (answers ?? Array.Empty<GameInteractionAnswer>()).ToArray();
        if (copied.Any(value => value is null))
        {
            throw new ArgumentException("Interaction answers cannot contain null values.", nameof(answers));
        }

        if (cancelled && copied.Length > 0)
        {
            throw new ArgumentException("A cancelled interaction cannot contain answers.", nameof(answers));
        }

        Answers = Array.AsReadOnly(copied);
    }

    public bool Cancelled { get; }

    public IReadOnlyList<GameInteractionAnswer> Answers { get; }
}

public sealed class GameInteractionCompleted
{
    public GameInteractionCompleted(GameInteractionRequest request, GameInteractionResponse response)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }

    public GameInteractionRequest Request { get; }

    public GameInteractionResponse Response { get; }
}

public interface IGameInteractionBroker
{
    ValueTask<GameInteractionResponse> PromptAsync(
        GameInteractionRequest request,
        CancellationToken cancellationToken);
}

public sealed class StructuredInteractionExtension : IGameAgentExtension
{
    private const string InputSchema = """
        {
          "type":"object",
          "required":["questions"],
          "properties":{
            "questions":{
              "type":"array",
              "minItems":1,
              "maxItems":8,
              "items":{
                "type":"object",
                "required":["id","prompt","options"],
                "properties":{
                  "id":{"type":"string","minLength":1,"maxLength":128},
                  "prompt":{"type":"string","minLength":1,"maxLength":8192},
                  "multiSelect":{"type":"boolean"},
                  "allowCustomAnswer":{"type":"boolean"},
                  "payload":{},
                  "options":{
                    "type":"array",
                    "minItems":2,
                    "maxItems":8,
                    "items":{
                      "type":"object",
                      "required":["id","label","description"],
                      "properties":{
                        "id":{"type":"string","minLength":1,"maxLength":128},
                        "label":{"type":"string","minLength":1,"maxLength":256},
                        "description":{"type":"string","minLength":1,"maxLength":4096},
                        "recommended":{"type":"boolean"},
                        "payload":{}
                      },
                      "additionalProperties":false
                    }
                  }
                },
                "additionalProperties":false
              }
            }
          },
          "additionalProperties":false
        }
        """;

    private readonly IGameInteractionBroker _broker;
    private readonly string _toolName;

    public StructuredInteractionExtension(IGameInteractionBroker broker, string toolName = "ask_player")
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _toolName = string.IsNullOrWhiteSpace(toolName)
            ? throw new ArgumentException("A tool name is required.", nameof(toolName))
            : toolName;
    }

    public static GameAgentExtensionChannel<GameInteractionRequest> InteractionStarted { get; } =
        new("interaction.started");

    public static GameAgentExtensionChannel<GameInteractionCompleted> InteractionCompleted { get; } =
        new("interaction.completed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.interaction",
        "1.0.0",
        "Structured player questions and choices for engine or server hosts.",
        new[] { "interaction", "recommended-actions", "headless-host" });

    public void Configure(GameAgentExtensionApi api)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        api.RegisterPromptFragment(
            "interaction-guidance",
            $"Use {_toolName} only when the agent cannot safely continue without a player decision. "
            + "Group related questions in one call. Mark at most one option per question as recommended and explain every option's trade-off.");
        api.RegisterToolProvider(
            "interaction-tool",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                new[] { CreateTool(api, context) }));
    }

    private AgentTool CreateTool(GameAgentExtensionApi api, GameAgentExtensionRunContext runContext) =>
        new(
            new ToolDefinition(
                _toolName,
                "Ask the player one or more bounded structured questions and wait for their answers.",
                InputSchema),
            async (arguments, execution, cancellationToken) =>
            {
                GameInteractionRequest request;
                try
                {
                    request = ParseRequest(arguments, execution, runContext.Input);
                }
                catch (ArgumentException exception)
                {
                    return ToolResult.Error(exception.Message);
                }

                await api.PublishAsync(InteractionStarted, request, cancellationToken).ConfigureAwait(false);
                var response = await _broker.PromptAsync(request, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The interaction broker returned null.");
                ValidateResponse(request, response);
                await api.PublishAsync(
                    InteractionCompleted,
                    new GameInteractionCompleted(request, response),
                    cancellationToken).ConfigureAwait(false);
                return new ToolResult(new AgentContent[]
                {
                    new JsonContent(JsonSerializer.Serialize(new
                    {
                        requestId = request.RequestId,
                        cancelled = response.Cancelled,
                        answers = response.Answers.Select(answer => new
                        {
                            questionId = answer.QuestionId,
                            selectedOptionIds = answer.SelectedOptionIds,
                            customAnswer = answer.CustomAnswer,
                        }),
                    })),
                });
            },
            ToolRisk.NonIdempotentWrite,
            ToolExecutionMode.Sequential);

    private static GameInteractionRequest ParseRequest(
        JsonElement root,
        ToolExecutionContext execution,
        GameInput input)
    {
        var questions = new List<GameInteractionQuestion>();
        foreach (var element in root.GetProperty("questions").EnumerateArray())
        {
            var options = new List<GameInteractionOption>();
            foreach (var option in element.GetProperty("options").EnumerateArray())
            {
                options.Add(new GameInteractionOption(
                    option.GetProperty("id").GetString() ?? string.Empty,
                    option.GetProperty("label").GetString() ?? string.Empty,
                    option.GetProperty("description").GetString() ?? string.Empty,
                    option.TryGetProperty("recommended", out var recommended) && recommended.GetBoolean(),
                    option.TryGetProperty("payload", out var optionPayload) ? optionPayload.GetRawText() : null));
            }

            questions.Add(new GameInteractionQuestion(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("prompt").GetString() ?? string.Empty,
                options,
                element.TryGetProperty("multiSelect", out var multiSelect) && multiSelect.GetBoolean(),
                !element.TryGetProperty("allowCustomAnswer", out var allowCustom) || allowCustom.GetBoolean(),
                element.TryGetProperty("payload", out var payload) ? payload.GetRawText() : null));
        }

        return new GameInteractionRequest(
            GameExtensionOperationIds.Create(
                "oga-interaction-v1:",
                "ask_player",
                input,
                execution),
            input,
            questions);
    }

    private static void ValidateResponse(GameInteractionRequest request, GameInteractionResponse response)
    {
        if (response.Cancelled)
        {
            return;
        }

        var questions = request.Questions.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var duplicate = response.Answers.GroupBy(value => value.QuestionId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"The interaction broker returned duplicate answers for '{duplicate.Key}'.");
        }

        if (response.Answers.Count != request.Questions.Count)
        {
            throw new InvalidOperationException("The interaction broker must answer every question or cancel the interaction.");
        }

        foreach (var answer in response.Answers)
        {
            if (!questions.TryGetValue(answer.QuestionId, out var question))
            {
                throw new InvalidOperationException($"The interaction broker answered unknown question '{answer.QuestionId}'.");
            }

            if (!question.MultiSelect && answer.SelectedOptionIds.Count > 1)
            {
                throw new InvalidOperationException($"Question '{question.Id}' does not allow multiple selections.");
            }

            var validIds = new HashSet<string>(question.Options.Select(value => value.Id), StringComparer.Ordinal);
            if (answer.SelectedOptionIds.Any(value => !validIds.Contains(value)))
            {
                throw new InvalidOperationException($"Question '{question.Id}' contains an unknown selected option.");
            }

            if (!question.AllowCustomAnswer && answer.CustomAnswer is not null)
            {
                throw new InvalidOperationException($"Question '{question.Id}' does not allow a custom answer.");
            }

            if (answer.SelectedOptionIds.Count == 0 && string.IsNullOrWhiteSpace(answer.CustomAnswer))
            {
                throw new InvalidOperationException($"Question '{question.Id}' requires a selection or custom answer.");
            }
        }
    }
}
