using OpenGameAgent.Attachments;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class ImageValidationTests
{
    [Fact]
    public void MessageImageCountAndAggregateBytesAreBounded()
    {
        var message = Message(
            Reference("one", bytes: 4, width: 1, height: 1),
            Reference("two", bytes: 4, width: 1, height: 1));

        Assert.Throws<AgentLimitException>(() => AgentValidation.ValidateMessages(
            new[] { message },
            new AgentLimits { MaxImagesPerMessage = 1 }));
        Assert.Throws<AgentLimitException>(() => AgentValidation.ValidateMessages(
            new[] { message },
            new AgentLimits { MaxImageBytes = 4, MaxImageBytesPerMessage = 7 }));
    }

    [Fact]
    public void AttachmentPixelsAndBytesAreCheckedWithoutLoadingTheObject()
    {
        var message = Message(Reference("large", bytes: 11, width: 4, height: 3));

        Assert.Throws<AgentLimitException>(() => AgentValidation.ValidateMessages(
            new[] { message },
            new AgentLimits { MaxImageBytes = 10 }));
        Assert.Throws<AgentLimitException>(() => AgentValidation.ValidateMessages(
            new[] { message },
            new AgentLimits { MaxImagePixels = 11 }));
    }

    [Fact]
    public void InlineImageLimitsUseDecodedBytesAndApplyToToolResults()
    {
        var result = new ToolResult(new AgentContent[]
        {
            new BinaryContent(AgentMediaKind.Image, "AQIDBA==", GameImageMediaTypes.Png),
        });

        var call = new ToolCallContent("call", "inspect", "{}");
        var message = AgentMessage.ToolResult(call, result, DateTimeOffset.UnixEpoch);
        Assert.Throws<AgentLimitException>(() => AgentValidation.ValidateMessages(
            new[] { message },
            new AgentLimits { MaxImageBytes = 3 }));
        AgentValidation.ValidateMessages(new[] { message }, new AgentLimits { MaxImageBytes = 4 });
    }

    [Fact]
    public void ImagesAreLimitedToUserAndToolResultMessages()
    {
        var image = Reference("role", bytes: 4, width: 1, height: 1);
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[] { image },
            DateTimeOffset.UnixEpoch,
            model: "model",
            stopReason: ModelStopReason.Stop);

        Assert.Throws<ArgumentException>(() => AgentValidation.ValidateMessages(
            new[] { assistant },
            new AgentLimits()));
        Assert.Throws<ArgumentException>(() => AgentValidator.ValidateResponse(
            new ModelResponse(new AgentContent[] { image }, ModelStopReason.Stop),
            new AgentLimits()));
    }

    [Fact]
    public void InlineImageProgressIsStillBounded()
    {
        var progress = new ToolProgress(content: new AgentContent[]
        {
            new BinaryContent(AgentMediaKind.Image, "AQI=", GameImageMediaTypes.Png),
        });

        AgentValidator.ValidateProgress(progress, new AgentLimits());
        Assert.Throws<AgentLimitException>(() => AgentValidator.ValidateProgress(
            progress,
            new AgentLimits { MaxImageBytes = 1, MaxImageBytesPerMessage = 1 }));
        AgentValidator.ValidateProgress(
            new ToolProgress(content: new AgentContent[] { Reference("progress", 1, 1, 1) }),
            new AgentLimits());
    }

    private static AgentMessage Message(params AgentContent[] content) => new(
        AgentRole.User,
        content,
        DateTimeOffset.UnixEpoch);

    private static ImageAttachmentContent Reference(
        string suffix,
        int bytes,
        int width,
        int height) => new(new GameImageAttachment(
            "sha256:" + suffix,
            GameImageMediaTypes.Png,
            bytes,
            width,
            height));
}
