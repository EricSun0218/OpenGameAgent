using System.Collections;
using System.Text.Json;

namespace GameAgent.Workflow.Tests;

internal static class WorkflowTestData
{
    public static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public static JsonElement StringSchema(int maxLength = 128)
    {
        return Json(
            $$"""
            {
              "type": "string",
              "maxLength": {{maxLength}}
            }
            """);
    }

    public static JsonElement IntegerSchema(
        int minimum = -1000,
        int maximum = 1000)
    {
        return Json(
            $$"""
            {
              "type": "integer",
              "minimum": {{minimum}},
              "maximum": {{maximum}}
            }
            """);
    }

    public static JsonElement SeedSchema()
    {
        return Json(
            """
            {
              "type": "object",
              "properties": {
                "seed": {
                  "type": "string",
                  "maxLength": 64
                }
              },
              "required": ["seed"],
              "additionalProperties": false
            }
            """);
    }

    public static JsonElement ForeachItemSchema()
    {
        return Json(
            """
            {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "maxLength": 64
                },
                "value": {
                  "type": "string",
                  "maxLength": 64
                }
              },
              "required": ["id", "value"],
              "additionalProperties": false
            }
            """);
    }

    public static JsonElement ForeachInputSchema(int maxItems = 8)
    {
        return Json(
            $$"""
            {
              "type": "object",
              "properties": {
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": {
                        "type": "string",
                        "maxLength": 64
                      },
                      "value": {
                        "type": "string",
                        "maxLength": 64
                      }
                    },
                    "required": ["id", "value"],
                    "additionalProperties": false
                  },
                  "maxItems": {{maxItems}}
                }
              },
              "required": ["items"],
              "additionalProperties": false
            }
            """);
    }

    public static JsonElement StringArraySchema(int maxItems = 8)
    {
        return Json(
            $$"""
            {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 128
              },
              "maxItems": {{maxItems}}
            }
            """);
    }

    public static JsonElement LoopValueSchema()
    {
        return Json(
            """
            {
              "type": "object",
              "properties": {
                "value": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 100
                },
                "done": {
                  "type": "boolean"
                }
              },
              "required": ["value", "done"],
              "additionalProperties": false
            }
            """);
    }

    public static JsonElement ReduceInputSchema(int maxItems = 8)
    {
        return Json(
            $$"""
            {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "stageId": {
                    "type": "string",
                    "maxLength": 128
                  },
                  "instanceId": {
                    "type": "string",
                    "maxLength": 80
                  },
                  "output": {
                    "type": "string",
                    "maxLength": 128
                  }
                },
                "required": ["stageId", "instanceId", "output"],
                "additionalProperties": false
              },
              "maxItems": {{maxItems}}
            }
            """);
    }
}

internal sealed class LyingCollection<T> : ICollection<T>
{
    private readonly IReadOnlyList<T> _items;

    public LyingCollection(params T[] items)
    {
        _items = items;
    }

    public int Count => 0;

    public bool IsReadOnly => true;

    public IEnumerator<T> GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool Contains(T item)
    {
        return false;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        throw new NotSupportedException();
    }

    public void Add(T item)
    {
        throw new NotSupportedException();
    }

    public bool Remove(T item)
    {
        throw new NotSupportedException();
    }

    public void Clear()
    {
        throw new NotSupportedException();
    }
}
