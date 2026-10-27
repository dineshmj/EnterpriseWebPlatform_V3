using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EnterpriseWebPlatform.Tests.Bian;

// ┌─ :: ARCHITECTURE :: BIAN's own file is the judge ──────────────────────────────────────────────
// │ Why : "BIAN-compatible" must mean "valid against what BIAN publishes", not "looks like it".
// │ How : Loads BIAN's OpenAPI file exactly as released (doc/bian, pinned to a commit), turns the
// │       YAML into JSON (plain scalars keep their type), and validates a /v2 body against one of
// │       its component schemas with a JSON Schema validator. BIAN sets no additionalProperties,
// │       so the check is on what EWP sends: names, nesting, types and enumerated values.
// │ Ref : doc/bian/README.md | OpenAPI 3.0.1 schema objects (a JSON Schema subset)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class BianSchema
{
    public static IReadOnlyList<string> Validate<T>(string bianFile, string componentSchema, T body, JsonSerializerOptions options)
    {
        var document = Load(bianFile);
        document["$ref"] = $"#/components/schemas/{componentSchema}";
        // OpenAPI 3.0 schema objects follow JSON Schema Draft-07's rules most closely: unknown
        // keywords (openapi, paths, example) are annotations, and $ref ignores its siblings.
        var schema = JsonSchema.FromText(document.ToJsonString(), new BuildOptions { Dialect = Dialect.Draft07 });

        var instance = JsonDocument.Parse(JsonSerializer.Serialize(body, options)).RootElement;
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });

        return result.IsValid
            ? []
            : (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .ToList();
    }

    private static JsonObject Load(string bianFile)
    {
        var yaml = new YamlStream();
        using (var reader = File.OpenText(Path.Combine(AppContext.BaseDirectory, "Bian", bianFile)))
            yaml.Load(reader);
        return (JsonObject)ToJson(yaml.Documents[0].RootNode)!;
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c =>
            KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, ToJson(c.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => null
    };

    // A plain (unquoted) scalar keeps its YAML type; a quoted one is always text.
    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
            return JsonValue.Create(text);
        if (text is "null" or "~" or "")
            return null;
        if (text is "true" or "false")
            return JsonValue.Create(text == "true");
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            return JsonValue.Create(whole);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(text);
    }
}