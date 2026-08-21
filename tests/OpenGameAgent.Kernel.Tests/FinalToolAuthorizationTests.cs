using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class FinalToolAuthorizationTests
{
    [Fact]
    public async Task FinalAuthorizationSeesPostRewriteArgumentsAndBlocksBeforeExecutor()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("call", "write", "{\"value\":1}")),
            Responses.Text("done"));
        var executed = false;
        var authorizedValue = 0;
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeToolCallAsync = (_, _) =>
                    new ValueTask<ToolCallDecision?>(ToolCallDecision.Allow("{\"value\":2}")),
                AuthorizeToolCallAsync = (context, _) =>
                {
                    authorizedValue = context.Arguments.GetProperty("value").GetInt32();
                    return new ValueTask<ToolCallDecision?>(ToolCallDecision.Block("approval required"));
                },
            },
        };
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("written"));
            },
            ToolRisk.NonIdempotentWrite,
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}"));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, authorizedValue);
        Assert.False(executed);
        Assert.Contains(result.NewMessages, message => message.Role == AgentRole.Tool && message.IsError);
    }

    [Fact]
    public async Task FinalAuthorizationCannotRewriteArguments()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("call", "write", "{}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                AuthorizeToolCallAsync = (_, _) =>
                    new ValueTask<ToolCallDecision?>(ToolCallDecision.Allow("{}")),
            },
        };
        options.Tools.Add(Responses.Tool("write", (_, _, _) =>
        {
            executed = true;
            return new ValueTask<ToolResult>(Responses.Result("written"));
        }));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(executed);
        var error = Assert.Single(result.NewMessages, message => message.Role == AgentRole.Tool);
        Assert.True(error.IsError);
        Assert.Contains("cannot rewrite", Assert.IsType<TextContent>(Assert.Single(error.Content)).Text, StringComparison.Ordinal);
    }
}
