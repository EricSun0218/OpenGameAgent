using System.Globalization;
using System.Text.Json;

namespace GameAgent.Tests;

public sealed class SchemaReferenceTests
{
    private const string SchemaBaseUri =
        "https://raw.githubusercontent.com/EricSun0218/"
        + "game-agent-runtime/main/schemas/";

    [Fact]
    public void SchemaIdsAndReferencesResolveEntirelyFromTheLocalSchemaSet()
    {
        var documents = Directory
            .GetFiles(
                FixtureFiles.SchemaDirectory,
                "*.schema.json",
                SearchOption.TopDirectoryOnly)
            .Select(
                path => new
                {
                    Path = path,
                    Document = JsonDocument.Parse(File.ReadAllText(path))
                })
            .ToArray();

        try
        {
            var documentsByUri = documents.ToDictionary(
                item => item.Document.RootElement
                    .GetProperty("$id")
                    .GetString()
                    ?? throw new InvalidDataException("Schema $id is null."),
                item => item.Document,
                StringComparer.Ordinal);

            Assert.Equal(documents.Length, documentsByUri.Count);
            foreach (var item in documents)
            {
                var expectedId = SchemaBaseUri + Path.GetFileName(item.Path);
                var actualId = item.Document.RootElement
                    .GetProperty("$id")
                    .GetString();
                Assert.Equal(expectedId, actualId);

                var baseUri = new Uri(expectedId, UriKind.Absolute);
                foreach (var reference in EnumerateReferences(
                             item.Document.RootElement))
                {
                    var resolved = new Uri(baseUri, reference);
                    var documentUri = resolved.GetLeftPart(UriPartial.Path);
                    Assert.True(
                        documentsByUri.TryGetValue(
                            documentUri,
                            out var targetDocument),
                        $"Schema reference '{reference}' did not resolve locally.");
                    AssertJsonPointerExists(
                        targetDocument.RootElement,
                        resolved.Fragment);
                }
            }
        }
        finally
        {
            foreach (var item in documents)
            {
                item.Document.Dispose();
            }
        }
    }

    private static IEnumerable<string> EnumerateReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals("$ref"))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            "Schema $ref must be a string.");
                    }

                    yield return property.Value.GetString()
                        ?? throw new InvalidDataException(
                            "Schema $ref is null.");
                }

                foreach (var reference in EnumerateReferences(property.Value))
                {
                    yield return reference;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var reference in EnumerateReferences(item))
                {
                    yield return reference;
                }
            }
        }
    }

    private static void AssertJsonPointerExists(
        JsonElement root,
        string fragment)
    {
        if (string.IsNullOrEmpty(fragment) || fragment == "#")
        {
            return;
        }

        if (!fragment.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Schema references must use JSON Pointer fragments.");
        }

        var current = root;
        var pointer = Uri.UnescapeDataString(fragment[2..]);
        foreach (var encodedToken in pointer.Split('/'))
        {
            var token = DecodePointerToken(encodedToken);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out var property))
                {
                    throw new InvalidDataException(
                        $"Schema pointer token '{token}' does not exist.");
                }

                current = property;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            throw new InvalidDataException(
                $"Schema pointer token '{token}' cannot be resolved.");
        }
    }

    private static string DecodePointerToken(string token)
    {
        for (var index = 0; index < token.Length; index++)
        {
            if (token[index] == '~'
                && (index + 1 >= token.Length
                    || token[index + 1] is not ('0' or '1')))
            {
                throw new InvalidDataException(
                    "Schema pointer contains an invalid escape.");
            }
        }

        return token
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
    }
}
