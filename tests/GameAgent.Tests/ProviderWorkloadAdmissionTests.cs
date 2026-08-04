using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class ProviderWorkloadAdmissionTests
{
    [Fact]
    public async Task BackgroundLimitLeavesCapacityForInteractiveWork()
    {
        using var admission = new ProviderWorkloadAdmission(
            maximumConcurrentCalls: 3,
            maximumConcurrentBackgroundCalls: 2);
        using var first = await admission.AcquireAsync(
            ProviderWorkloadClasses.Background,
            CancellationToken.None);
        using var second = await admission.AcquireAsync(
            ProviderWorkloadClasses.Background,
            CancellationToken.None);
        var thirdBackground = admission.AcquireAsync(
                ProviderWorkloadClasses.Background,
                CancellationToken.None)
            .AsTask();

        await Task.Delay(20, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(thirdBackground.IsCompleted);
        using var interactive = await admission
            .AcquireAsync(
                ProviderWorkloadClasses.Interactive,
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(thirdBackground.IsCompleted);

        first.Dispose();
        using var admittedBackground = await thirdBackground.WaitAsync(
            TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledBackgroundWaitReleasesItsClassReservation()
    {
        using var admission = new ProviderWorkloadAdmission(
            maximumConcurrentCalls: 2,
            maximumConcurrentBackgroundCalls: 1);
        using var firstInteractive = await admission.AcquireAsync(
            ProviderWorkloadClasses.Interactive,
            CancellationToken.None);
        using var secondInteractive = await admission.AcquireAsync(
            ProviderWorkloadClasses.Interactive,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = admission.AcquireAsync(
                ProviderWorkloadClasses.Background,
                cancellation.Token)
            .AsTask();
        await Task.Delay(20, cancellationToken: TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelled);
        firstInteractive.Dispose();
        using var next = await admission
            .AcquireAsync(
                ProviderWorkloadClasses.Background,
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other")]
    public async Task UnknownOrMissingDirectClassIsHandledExplicitly(
        string? workloadClass)
    {
        using var admission = new ProviderWorkloadAdmission(1, null);
        if (string.IsNullOrWhiteSpace(workloadClass))
        {
            using var lease = await admission.AcquireAsync(
                workloadClass!,
                CancellationToken.None);
            return;
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => admission
                .AcquireAsync(workloadClass, CancellationToken.None)
                .AsTask());
    }
}
