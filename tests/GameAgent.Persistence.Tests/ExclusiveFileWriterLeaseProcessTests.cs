using System.Diagnostics;
using GameAgent.Persistence.WriterProbe;

namespace GameAgent.Persistence.Tests;

public sealed class ExclusiveFileWriterLeaseProcessTests
{
    [Fact]
    public async Task ActiveWriterFencesChildAcrossSharedReaderLifetimeAndReleasesOnDispose()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "session.journal");
        Directory.CreateDirectory(directory);
        FileSessionStore? parent = null;
        try
        {
            parent = new FileSessionStore(path);
            await parent.FlushAsync();

            AssertSidecarRejectsCompetingHandle(path + ".writer.lock");

            await using (var reader = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.Equal(0, reader.Length);
                var whileReaderIsOpen =
                    await RunChildWriterProbeAsync(path);
                AssertWriterUnavailable(whileReaderIsOpen);
            }

            var afterReaderIsClosed =
                await RunChildWriterProbeAsync(path);
            AssertWriterUnavailable(afterReaderIsClosed);

            await parent.DisposeAsync();
            parent = null;

            var afterParentIsDisposed =
                await RunChildWriterProbeAsync(path);
            Assert.Equal(
                0,
                afterParentIsDisposed.ExitCode);
        }
        finally
        {
            if (parent is not null)
            {
                await parent.DisposeAsync();
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static void AssertSidecarRejectsCompetingHandle(
        string sidecarPath)
    {
        Assert.True(File.Exists(sidecarPath));
        var exception = Record.Exception(
            () =>
            {
                using var competingHandle = new FileStream(
                    sidecarPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            });

        Assert.IsAssignableFrom<IOException>(exception);
    }

    private static void AssertWriterUnavailable(ProbeResult result)
    {
        Assert.True(
            result.ExitCode == Program.WriterUnavailableExitCode,
            $"Expected child exit code {Program.WriterUnavailableExitCode}, "
            + $"but received {result.ExitCode}.{Environment.NewLine}"
            + $"stdout: {result.StandardOutput}{Environment.NewLine}"
            + $"stderr: {result.StandardError}");
    }

    private static async Task<ProbeResult> RunChildWriterProbeAsync(
        string path)
    {
        var probeAssemblyPath = typeof(ProbeAssembly).Assembly.Location;
        var runtimeConfigurationPath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "GameAgent.Persistence.Tests.runtimeconfig.json");
        var dependenciesPath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "GameAgent.Persistence.Tests.deps.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfigurationPath);
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(dependenciesPath);
        startInfo.ArgumentList.Add(probeAssemblyPath);
        startInfo.ArgumentList.Add("acquire-session-writer");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The writer probe process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProbeResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record ProbeResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
