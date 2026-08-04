namespace GameAgent.Tests;

internal static class FixtureFiles
{
    public static string Read(params string[] path)
    {
        var parts = new[] { AppContext.BaseDirectory, "fixtures" }.Concat(path).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    public static string SchemaDirectory =>
        Path.Combine(AppContext.BaseDirectory, "schemas");
}
