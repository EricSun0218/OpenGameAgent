using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ToolInputSafetyTests
{
    [Fact]
    public void ValidatorAcceptsTheDocumentedStrictSubsetRecursively()
    {
        var schema = Json(
            """
            {
              "type": "object",
              "title": "Move",
              "description": "A strict tool schema.",
              "properties": {
                "entityId": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 3,
                  "enum": ["npc", "玩家"]
                },
                "count": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 4,
                  "const": 2.0
                },
                "weights": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 2,
                  "items": {
                    "type": "number",
                    "minimum": -1.5,
                    "maximum": 10
                  }
                },
                "enabled": { "type": "boolean" },
                "marker": { "type": "null" }
              },
              "required": ["entityId", "count", "weights", "enabled", "marker"],
              "additionalProperties": false
            }
            """);
        var arguments = Json(
            """
            {
              "entityId": "玩家",
              "count": 2,
              "weights": [-1.50, 1e1],
              "enabled": true,
              "marker": null
            }
            """);

        var result = new ToolArgumentValidator().Validate(schema, arguments);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidatorReportsBoundedStructuredErrorsWithoutArgumentValues()
    {
        const string secretValue = "DO-NOT-LEAK-THIS";
        const string unexpectedName = "DO-NOT-LEAK-NAME";
        var schema = Json(
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "minLength": 2, "maxLength": 4 },
                "count": { "type": "integer", "minimum": 1, "maximum": 2 },
                "items": {
                  "type": "array",
                  "minItems": 2,
                  "maxItems": 2,
                  "items": { "type": "boolean" }
                },
                "missing": { "type": "boolean" }
              },
              "required": ["missing"],
              "additionalProperties": false
            }
            """);
        var arguments = Json(
            $$"""
            {
              "name": "{{secretValue}}",
              "count": 2.5,
              "items": [true],
              "{{unexpectedName}}": true
            }
            """);

        var result = new ToolArgumentValidator().Validate(schema, arguments);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, item => item.Code == "argument_required_property_missing");
        Assert.Contains(result.Errors, item => item.Code == "argument_max_length_exceeded");
        Assert.Contains(result.Errors, item => item.Code == "argument_type_mismatch");
        Assert.Contains(result.Errors, item => item.Code == "argument_min_items_not_met");
        Assert.Contains(
            result.Errors,
            item => item.Code == "argument_additional_property_not_allowed");
        Assert.Contains(
            result.Errors,
            item => item.Code == "argument_required_property_missing"
                    && item.InstancePath == "$/missing"
                    && item.SchemaPath == "$/required");
        Assert.Contains(
            result.Errors,
            item => item.Code == "argument_max_length_exceeded"
                    && item.InstancePath == "$/name"
                    && item.SchemaPath == "$/properties/name/maxLength");
        Assert.All(
            result.Errors,
            error =>
            {
                var rendered = $"{error.Code}|{error.InstancePath}|{error.SchemaPath}";
                Assert.DoesNotContain(secretValue, rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(unexpectedName, rendered, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void ValidatorCompilesEverySchemaBranchAndFailsClosedOnUnsupportedKeywords()
    {
        var pattern = new ToolArgumentValidator().Validate(
            Json(
                """
                {
                  "type": "object",
                  "properties": {
                    "unused": { "type": "string", "pattern": ".*" }
                  }
                }
                """),
            Json("{}"));
        var unknown = new ToolArgumentValidator().Validate(
            Json("""{"type":"object","vendorExtension":true}"""),
            Json("{}"));
        var unsupported = new ToolArgumentValidator().Validate(
            Json("""{"type":"object","oneOf":[{"type":"object"}]}"""),
            Json("{}"));

        Assert.Equal(
            "schema_pattern_unsupported",
            Assert.Single(pattern.Errors).Code);
        Assert.Equal(
            "schema_keyword_unknown",
            Assert.Single(unknown.Errors).Code);
        Assert.Equal(
            "schema_keyword_unsupported",
            Assert.Single(unsupported.Errors).Code);
    }

    [Fact]
    public void ValidatorRejectsMalformedSchemasAndCapsErrorVolume()
    {
        var result = new ToolArgumentValidator(
                new ToolArgumentValidationOptions(maxErrors: 2))
            .Validate(
                Json(
                    """
                    {
                      "type": "string",
                      "properties": [],
                      "required": "x",
                      "additionalProperties": {},
                      "minimum": "zero",
                      "minLength": -1,
                      "maxLength": 1
                    }
                    """),
                Json("\"x\""));

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.StartsWith("schema_", error.Code));
    }

    [Fact]
    public void ValidatorUsesNumericValueEqualityAndUnicodeScalarStringLength()
    {
        var numeric = new ToolArgumentValidator().Validate(
            Json("""{"type":"integer","enum":[1.0],"const":1e0}"""),
            Json("1"));
        var unicode = new ToolArgumentValidator().Validate(
            Json("""{"type":"string","minLength":1,"maxLength":1}"""),
            Json("\"😀\""));

        Assert.True(numeric.IsValid);
        Assert.True(unicode.IsValid);
    }

    [Fact]
    public void ValidatorFailsClosedOnNumbersOutsideItsDocumentedBoundedRepresentation()
    {
        var result = new ToolArgumentValidator().Validate(
            Json("""{"type":"number","enum":[1e1000001]}"""),
            Json("1"));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            item => item.Code == "schema_enum_number_out_of_supported_range");
    }

    [Fact]
    public void ConflictResolverHandlesNestedPathsEncodingOrderingAndNumericNormalization()
    {
        var resolver = new ConflictScopeResolver();
        var arguments = Json(
            """
            {
              "entityId": "npc/一",
              "owner": { "id": "player:7" },
              "slot": 2.0
            }
            """);

        var result = resolver.Resolve(
            new[]
            {
                "inventory:{owner.id}",
                "entity:{entityId}",
                "slot:{slot}",
                "entity:{entityId}"
            },
            arguments);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[]
            {
                "entity:npc%2F%E4%B8%80",
                "inventory:player%3A7",
                "slot:2"
            },
            result.Keys);
    }

    [Fact]
    public void TrustedRuntimeBindingsTakePrecedenceAndCannotBeSpoofedByArguments()
    {
        var arguments = Json(
            """
            {
              "agentId": "model-controlled",
              "entityId": "npc-1"
            }
            """);
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agentId"] = "agent/trusted",
            ["worldId"] = "world:7"
        };

        var result = new ConflictScopeResolver().Resolve(
            new[]
            {
                "world:{worldId}",
                "agent:{agentId}",
                "entity:{entityId}"
            },
            arguments,
            bindings);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[]
            {
                "agent:agent%2Ftrusted",
                "entity:npc-1",
                "world:world%3A7"
            },
            result.Keys);
        Assert.DoesNotContain(
            result.Keys,
            key => key.Contains("model-controlled", StringComparison.Ordinal));
    }

    [Fact]
    public void ReservedRuntimeBindingNeverFallsBackToModelArguments()
    {
        var result = new ConflictScopeResolver().Resolve(
            new[] { "agent:{agentId}" },
            Json("""{"agentId":"spoofed"}"""));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Keys);
        Assert.Equal(
            "conflict_runtime_binding_missing",
            Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void RuntimeBindingLimitsFailClosedWithoutEchoingValues()
    {
        const string sensitive = "SENSITIVE-BINDING";
        var resolver = new ConflictScopeResolver(
            new ConflictScopeResolverOptions(
                maxTrustedBindings: 1,
                maxScalarUtf8Bytes: 8));
        var countResult = resolver.Resolve(
            new[] { "agent:{agentId}" },
            Json("{}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agentId"] = "a",
                ["worldId"] = "b"
            });
        var valueResult = resolver.Resolve(
            new[] { "agent:{agentId}" },
            Json("{}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agentId"] = sensitive
            });

        Assert.Equal(
            "conflict_runtime_binding_count_exceeded",
            Assert.Single(countResult.Errors).Code);
        var error = Assert.Single(valueResult.Errors);
        Assert.Equal("conflict_runtime_binding_value_invalid", error.Code);
        Assert.DoesNotContain(
            sensitive,
            $"{error.Code}|{error.InstancePath}|{error.SchemaPath}",
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"owner":{}}""", "conflict_scope_value_missing")]
    [InlineData("""{"owner":{"id":null}}""", "conflict_scope_value_type_unsupported")]
    [InlineData("""{"owner":{"id":[]}}""", "conflict_scope_value_type_unsupported")]
    [InlineData("""{"owner":{"id":{}}}""", "conflict_scope_value_type_unsupported")]
    public void ConflictResolverFailsClosedWithoutPartialKeys(
        string argumentsJson,
        string expectedCode)
    {
        var result = new ConflictScopeResolver().Resolve(
            new[] { "global", "inventory:{owner.id}" },
            Json(argumentsJson));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Keys);
        Assert.Contains(result.Errors, item => item.Code == expectedCode);
    }

    [Theory]
    [InlineData("entity:{owner..id}", "conflict_scope_path_invalid")]
    [InlineData("entity:{owner.id", "conflict_scope_template_invalid")]
    [InlineData("entity:owner.id}", "conflict_scope_template_invalid")]
    [InlineData("entity:{{owner.id}}", "conflict_scope_template_invalid")]
    public void ConflictResolverRejectsAmbiguousTemplates(
        string template,
        string expectedCode)
    {
        var result = new ConflictScopeResolver().Resolve(
            new[] { template },
            Json("""{"owner":{"id":"x"}}"""));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ConflictResolverEnforcesCountAndExpandedUtf8LengthLimits()
    {
        var countLimited = new ConflictScopeResolver(
            new ConflictScopeResolverOptions(maxScopes: 1));
        var countResult = countLimited.Resolve(
            new[] { "one", "two" },
            Json("{}"));
        var sizeLimited = new ConflictScopeResolver(
            new ConflictScopeResolverOptions(
                maxTemplateUtf8Bytes: 64,
                maxScalarUtf8Bytes: 64,
                maxKeyUtf8Bytes: 11));
        var sizeResult = sizeLimited.Resolve(
            new[] { "id:{value}" },
            Json("""{"value":"a/b/c"}"""));

        Assert.False(countResult.IsSuccess);
        Assert.Empty(countResult.Keys);
        Assert.Equal(
            "conflict_scope_count_exceeded",
            Assert.Single(countResult.Errors).Code);
        Assert.False(sizeResult.IsSuccess);
        Assert.Empty(sizeResult.Keys);
        Assert.Equal(
            "conflict_scope_key_size_exceeded",
            Assert.Single(sizeResult.Errors).Code);
    }

    [Fact]
    public void ConflictResolverLimitsCannotExceedActionWireCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConflictScopeResolverOptions(
                maxScopes: ProtocolLimits.MaxToolConflictScopes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConflictScopeResolverOptions(
                maxKeyUtf8Bytes:
                    ProtocolLimits.MaxActionExpectedEffectUnicodeScalars + 1));
    }

    [Fact]
    public void CombinedGuardUsesCatalogSchemaAndCatalogConflictTemplates()
    {
        var registry = new ToolCatalogRegistry();
        var descriptor = Descriptor(
            """
            {
              "type": "object",
              "properties": {
                "entityId": { "type": "string" }
              },
              "required": ["entityId"],
              "additionalProperties": false
            }
            """);
        descriptor.ConflictScopes.Add("entity:{entityId}");
        descriptor.ConflictScopes.Add("agent:{agentId}");
        var tool = Assert.Single(registry.Replace(new[] { descriptor }).Tools);

        var accepted = new ToolInputSafetyGuard().Validate(
            tool,
            Json("""{"entityId":"npc-1","agentId":"spoofed"}"""),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agentId"] = "agent-a"
            });
        var rejected = new ToolInputSafetyGuard().Validate(
            tool,
            Json("""{"other":"npc-1"}"""));

        Assert.False(accepted.IsValid);
        Assert.Empty(accepted.ResolvedConflictKeys);
        var valid = new ToolInputSafetyGuard().Validate(
            tool,
            Json("""{"entityId":"npc-1"}"""),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agentId"] = "agent-a"
            });
        Assert.True(valid.IsValid);
        Assert.Equal(
            new[] { "agent:agent-a", "entity:npc-1" },
            valid.ResolvedConflictKeys);
        Assert.False(rejected.IsValid);
        Assert.Empty(rejected.ResolvedConflictKeys);
    }

    private static ToolDescriptor Descriptor(string schema)
    {
        return new ToolDescriptor
        {
            Name = "world.inspect",
            Version = "1.0.0",
            Description = "Inspects one entity.",
            ParametersSchema = Json(schema),
            Effect = ToolEffects.PureRead,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 2_000,
            RetryPolicy = "never",
            IdempotencyPolicy = "none",
            Toolset = "tests",
            Visibility = "direct"
        };
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
