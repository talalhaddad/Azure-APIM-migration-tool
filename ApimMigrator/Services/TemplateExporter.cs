using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApimMigrator.Models;
using Azure.ResourceManager.ApiManagement;

namespace ApimMigrator.Services;

public enum ExportFormat { Arm, Terraform, PowerShell }

/// <summary>Generates deployment templates (ARM, Terraform/azapi, PowerShell) for the planned migration.</summary>
public static class TemplateExporter
{
    public const string ApiVersion = "2022-08-01";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly ModelReaderWriterOptions Wire = new("W");

    private sealed record Res(string Key, List<(string Collection, string Name)> Segments, JsonObject Properties, List<string> DependsOn)
    {
        public string Type => "Microsoft.ApiManagement/service/" + string.Join("/", Segments.Select(s => s.Collection));
        public string NamePath => string.Join("/", Segments.Select(s => s.Name));
        public string UrlPath => string.Concat(Segments.Select(s => $"/{s.Collection}/{Uri.EscapeDataString(s.Name)}"));
    }

    public static string FileExtension(ExportFormat f) => f switch
    {
        ExportFormat.Arm => "json",
        ExportFormat.Terraform => "tf",
        _ => "ps1"
    };

    public static string Export(ExportFormat format, ApimItem destination, IReadOnlyList<ApiModel> apis, MigrationOptions options)
    {
        var resources = BuildResources(apis, options, out var notes);
        return format switch
        {
            ExportFormat.Arm => RenderArm(destination, resources, notes),
            ExportFormat.Terraform => RenderTerraform(destination, resources, notes),
            _ => RenderPowerShell(destination, resources, notes)
        };
    }

    // ------------------------------------------------------------------ model

    private static List<Res> BuildResources(IReadOnlyList<ApiModel> apis, MigrationOptions options, out List<string> notes)
    {
        var list = new List<Res>();
        notes = new List<string>();
        var versionSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var api in apis)
        {
            var updateApiLevel = !api.ExistsOnDestination || options.UpdateExistingApiSettings;
            string? apiKey = null;

            if (updateApiLevel)
            {
                string? vsKey = null;
                if (api.VersionSet is not null && api.VersionSetName is not null)
                {
                    vsKey = Key("versionset", api.VersionSetName);
                    if (versionSets.Add(api.VersionSetName))
                    {
                        var vsProps = Props(api.VersionSet);
                        list.Add(new Res(vsKey, [("apiVersionSets", api.VersionSetName)], vsProps, []));
                    }
                }

                var p = Props(api.Source);
                foreach (var ro in new[] { "isCurrent", "isOnline", "apiRevision", "apiRevisionDescription", "apiVersionSet", "sourceApiId" })
                    p.Remove(ro);
                p["path"] = api.TargetPath ?? api.SourcePath;
                if (string.IsNullOrWhiteSpace(api.TargetServiceUrl)) p.Remove("serviceUrl");
                else p["serviceUrl"] = api.TargetServiceUrl;
                if (api.VersionSetName is not null)
                    p["apiVersionSetId"] = $"{{APIM_ID}}/apiVersionSets/{api.VersionSetName}";
                else
                    p.Remove("apiVersionSetId");

                apiKey = Key("api", api.Name);
                list.Add(new Res(apiKey, [("apis", api.Name)], p, vsKey is null ? [] : [vsKey]));

                if (options.IncludeSchemas)
                {
                    foreach (var (name, data) in api.Schemas)
                        list.Add(new Res(Key("schema", api.Name, name), [("apis", api.Name), ("schemas", name)], Props(data), [apiKey]));
                }

                if (options.IncludePolicies && !string.IsNullOrWhiteSpace(api.TargetApiPolicyXml))
                    list.Add(new Res(Key("apipolicy", api.Name), [("apis", api.Name), ("policies", "policy")],
                        PolicyProps(api.TargetApiPolicyXml), [apiKey]));
            }
            else
            {
                notes.Add($"API '{api.Name}' already exists on the destination; only its selected operations are included.");
            }

            foreach (var op in api.SelectedOperations)
            {
                var p = Props(op.Source);
                p.Remove("policies");
                p["urlTemplate"] = op.TargetUrlTemplate ?? op.SourceUrlTemplate;
                var opKey = Key("op", api.Name, op.Name);
                list.Add(new Res(opKey, [("apis", api.Name), ("operations", op.Name)], p, apiKey is null ? [] : [apiKey]));

                if (options.IncludePolicies && !string.IsNullOrWhiteSpace(op.TargetPolicyXml))
                    list.Add(new Res(Key("oppolicy", api.Name, op.Name), [("apis", api.Name), ("operations", op.Name), ("policies", "policy")],
                        PolicyProps(op.TargetPolicyXml), [opKey]));
            }
        }

        if (options.IncludePolicies)
        {
            var xmls = apis.SelectMany(a => a.SelectedOperations.Select(o => o.TargetPolicyXml).Append(a.TargetApiPolicyXml)).ToList();
            var nvs = xmls.SelectMany(PolicyHelper.GetNamedValues).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var backends = xmls.SelectMany(PolicyHelper.GetBackendIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (nvs.Count > 0)
                notes.Add($"Named values referenced by policies must exist on the destination (not exported because they may hold secrets): {string.Join(", ", nvs)}");
            if (backends.Count > 0)
                notes.Add($"Backends referenced by policies must exist on the destination: {string.Join(", ", backends)}");
        }

        return list;
    }

    private static JsonObject Props<T>(T model) where T : IPersistableModel<T>
    {
        var json = ModelReaderWriter.Write(model, Wire).ToString();
        var node = JsonNode.Parse(json) as JsonObject;
        return node?["properties"]?.DeepClone() as JsonObject ?? new JsonObject();
    }

    private static JsonObject PolicyProps(string xml) => new() { ["format"] = "rawxml", ["value"] = xml };

    private static string Key(params string[] parts) =>
        Regex.Replace(string.Join("_", parts), @"[^A-Za-z0-9_]", "_").ToLowerInvariant();

    /// <summary>Replaces the {APIM_ID} placeholder in string values.</summary>
    private static JsonObject WithApimId(JsonObject props, string apimIdExpression)
    {
        var clone = (JsonObject)props.DeepClone();
        Walk(clone, s => s.Replace("{APIM_ID}", apimIdExpression));
        return clone;
    }

    private static void Walk(JsonNode? node, Func<string, string> transform)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = transform(s);
                    else Walk(o[key], transform);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) a[i] = transform(s);
                    else Walk(a[i], transform);
                }
                break;
        }
    }

    // ------------------------------------------------------------------ ARM

    private static string RenderArm(ApimItem dest, List<Res> resources, List<string> notes)
    {
        var arr = new JsonArray();
        foreach (var r in resources)
        {
            var props = WithApimId(r.Properties, "{APIM_ID}");
            // Escape literal strings that start with '[' so ARM does not treat them as expressions.
            Walk(props, s => s.StartsWith('[') ? "[" + s : s);
            Walk(props, s => s.Contains("{APIM_ID}")
                ? $"[concat(resourceId('Microsoft.ApiManagement/service', parameters('apimName')), '{s.Replace("{APIM_ID}", "").Replace("'", "''")}')]"
                : s);

            var nameExpr = $"[concat(parameters('apimName'), '/{r.NamePath.Replace("'", "''")}')]";
            var deps = new JsonArray();
            foreach (var d in r.DependsOn)
            {
                var dep = resources.First(x => x.Key == d);
                var segs = string.Join(", ", dep.Segments.Select(s => $"'{s.Name.Replace("'", "''")}'"));
                deps.Add($"[resourceId('{dep.Type}', parameters('apimName'), {segs})]");
            }

            arr.Add(new JsonObject
            {
                ["type"] = r.Type,
                ["apiVersion"] = ApiVersion,
                ["name"] = nameExpr,
                ["dependsOn"] = deps,
                ["properties"] = props
            });
        }

        var template = new JsonObject
        {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            ["contentVersion"] = "1.0.0.0",
            ["metadata"] = new JsonObject
            {
                ["generator"] = "Azure APIM Migration Tool",
                ["generated"] = DateTime.UtcNow.ToString("u"),
                ["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())
            },
            ["parameters"] = new JsonObject
            {
                ["apimName"] = new JsonObject
                {
                    ["type"] = "string",
                    ["defaultValue"] = dest.Resource.Id.Name,
                    ["metadata"] = new JsonObject { ["description"] = "Destination API Management service name." }
                }
            },
            ["resources"] = arr
        };
        return template.ToJsonString(Indented);
    }

    // ------------------------------------------------------------------ Terraform (azapi)

    private static string RenderTerraform(ApimItem dest, List<Res> resources, List<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Azure APIM Migration Tool");
        foreach (var n in notes) sb.AppendLine($"# NOTE: {n}");
        sb.AppendLine();
        sb.AppendLine("terraform {");
        sb.AppendLine("  required_providers {");
        sb.AppendLine("    azapi = {");
        sb.AppendLine("      source  = \"Azure/azapi\"");
        sb.AppendLine("      version = \">= 1.13\"");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("provider \"azapi\" {}");
        sb.AppendLine();
        sb.AppendLine("variable \"apim_id\" {");
        sb.AppendLine("  type        = string");
        sb.AppendLine("  description = \"Resource ID of the destination API Management service.\"");
        sb.AppendLine($"  default     = \"{dest.Resource.Id}\"");
        sb.AppendLine("}");

        foreach (var r in resources)
        {
            var parent = r.Segments.Count == 1
                ? "var.apim_id"
                : $"\"${{var.apim_id}}{string.Concat(r.Segments.Take(r.Segments.Count - 1).Select(s => $"/{s.Collection}/{s.Name}"))}\"";
            var body = new JsonObject { ["properties"] = WithApimId(r.Properties, "${var.apim_id}") }.ToJsonString(Indented);
            // Escape Terraform template sequences that come from policy XML, then restore the intended variable reference.
            body = body.Replace("${", "$${").Replace("%{", "%%{").Replace("$${var.apim_id}", "${var.apim_id}");

            sb.AppendLine();
            sb.AppendLine($"resource \"azapi_resource\" \"{r.Key}\" {{");
            sb.AppendLine($"  type      = \"{r.Type}@{ApiVersion}\"");
            sb.AppendLine($"  name      = \"{r.Segments[^1].Name}\"");
            sb.AppendLine($"  parent_id = {parent}");
            sb.AppendLine("  schema_validation_enabled = false");
            if (r.DependsOn.Count > 0)
                sb.AppendLine($"  depends_on = [{string.Join(", ", r.DependsOn.Select(d => $"azapi_resource.{d}"))}]");
            sb.AppendLine("  body = jsondecode(<<-JSON");
            foreach (var line in body.Split('\n')) sb.AppendLine("    " + line.TrimEnd('\r'));
            sb.AppendLine("    JSON");
            sb.AppendLine("  )");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ PowerShell

    private static string RenderPowerShell(ApimItem dest, List<Res> resources, List<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Azure APIM Migration Tool");
        sb.AppendLine("# Requires the Az.Accounts module. Sign in first: Connect-AzAccount -Tenant <tenant-id>");
        foreach (var n in notes) sb.AppendLine($"# NOTE: {n}");
        sb.AppendLine();
        sb.AppendLine("param(");
        sb.AppendLine($"    [string]$ApimId = '{dest.Resource.Id}'");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$ApiVersion = '{ApiVersion}'");
        sb.AppendLine();
        sb.AppendLine("function Invoke-ApimPut([string]$RelativePath, [string]$Payload, [string]$Description) {");
        sb.AppendLine("    Write-Host \"PUT $Description\"");
        sb.AppendLine("    $Payload = $Payload.Replace('{APIM_ID}', $ApimId)");
        sb.AppendLine("    $resp = Invoke-AzRestMethod -Method PUT -Path \"$ApimId$($RelativePath)?api-version=$ApiVersion\" -Payload $Payload");
        sb.AppendLine("    if ($resp.StatusCode -ge 300) { throw \"Failed ($($resp.StatusCode)) on $($Description): $($resp.Content)\" }");
        sb.AppendLine("}");

        foreach (var r in resources)
        {
            var body = new JsonObject { ["properties"] = (JsonObject)r.Properties.DeepClone() }.ToJsonString(Indented);
            sb.AppendLine();
            sb.AppendLine($"Invoke-ApimPut -RelativePath '{r.UrlPath.Replace("'", "''")}' -Description '{r.Type.Split('/')[^1]} {r.NamePath.Replace("'", "''")}' -Payload @'");
            sb.AppendLine(body);
            sb.AppendLine("'@");
        }
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Deployment completed.' -ForegroundColor Green");
        return sb.ToString();
    }
}
