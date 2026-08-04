using GameAgent.Persistence;

namespace GameAgent.Persistence.WriterProbe;

public static class ProbeAssembly;

public static class Program
{
    public const int WriterUnavailableExitCode = 23;
    public const int UnexpectedFailureExitCode = 24;
    public const int InvalidArgumentsExitCode = 64;

    public static async Task<int> Main(string[] args)
    {
        if (args is not ["acquire-session-writer", var path]
            || string.IsNullOrWhiteSpace(path))
        {
            return InvalidArgumentsExitCode;
        }

        try
        {
            await using var store = new FileSessionStore(path);
            return 0;
        }
        catch (IOException)
        {
            return WriterUnavailableExitCode;
        }
        catch
        {
            return UnexpectedFailureExitCode;
        }
    }
}
