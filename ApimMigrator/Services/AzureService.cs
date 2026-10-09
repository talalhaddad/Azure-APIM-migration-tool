using ApimMigrator.Models;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ApiManagement;
using Azure.ResourceManager.ApiManagement.Models;

namespace ApimMigrator.Services;

public sealed class AzureService
{
    public async Task<AzureSession> LoginAsync(string? tenantId, bool useDeviceCode, Action<string> deviceCodeMessage, CancellationToken ct)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        AuthenticationRecord record;
        TokenCredential credential;
        if (useDeviceCode)
        {
            var dc = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = tenant,
                DeviceCodeCallback = (info, _) => { deviceCodeMessage(info.Message); return Task.CompletedTask; }
            });
            record = await dc.AuthenticateAsync(ct);
            credential = dc;
        }
        else
        {
            var ib = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions { TenantId = tenant });
            record = await ib.AuthenticateAsync(ct);
            credential = ib;
        }

        return new AzureSession
        {
            DisplayName = $"{record.Username} @ {record.TenantId}",
            TenantId = record.TenantId,
            Client = new ArmClient(credential)
        };
    }

    public async Task<List<SubscriptionItem>> GetSubscriptionsAsync(AzureSession session, CancellationToken ct)
    {
        var list = new List<SubscriptionItem>();
        await foreach (var s in session.Client.GetSubscriptions().GetAllAsync(ct))
            list.Add(new SubscriptionItem(s));
        return list.OrderBy(s => s.Resource.Data.DisplayName).ToList();
    }

    public async Task<List<ApimItem>> GetApimServicesAsync(AzureSession session, SubscriptionItem sub, CancellationToken ct)
    {
        var list = new List<ApimItem>();
        await foreach (var a in sub.Resource.GetApiManagementServicesAsync(cancellationToken: ct))
            list.Add(new ApimItem(a, session, sub));
        return list.OrderBy(a => a.Resource.Data.Name).ToList();
    }

    // ---------------------------------------------------------------- Discovery

    public async Task<List<ApiModel>> DiscoverAsync(ApiManagementServiceResource apim, IProgress<string> log, CancellationToken ct)
    {
        var result = new List<ApiModel>();
        await foreach (var api in apim.GetApis().GetAllAsync(cancellationToken: ct))
        {
            var name = api.Data.Name;
            if (name.Contains(";rev=", StringComparison.OrdinalIgnoreCase)) continue;

            log.Report($"Discovering API '{api.Data.DisplayName}'...");
            var model = new ApiModel { Name = name, Source = api.Data };

            model.ApiPolicyXml = await TryGetAsync(async () =>
                (await api.GetApiPolicies().GetAsync(PolicyName.Policy, PolicyExportFormat.RawXml, ct)).Value.Data.Value);
            model.OriginalBackendBaseUrl = PolicyHelper.GetValue(PolicyHelper.BackendBaseUrl, model.ApiPolicyXml);

            await foreach (var schema in api.GetApiSchemas().GetAllAsync(cancellationToken: ct))
                model.Schemas.Add((schema.Data.Name, schema.Data));

            if (api.Data.ApiVersionSetId is { } vsId)
            {
                model.VersionSetName = new ResourceIdentifier(vsId.ToString()).Name;
                model.VersionSet = await TryGetAsync(async () => (await apim.GetApiVersionSets().GetAsync(model.VersionSetName, ct)).Value.Data);
            }

            await foreach (var op in api.GetApiOperations().GetAllAsync(cancellationToken: ct))
            {
                var opModel = new OperationModel { Name = op.Data.Name, Source = op.Data };
                opModel.PolicyXml = await TryGetAsync(async () =>
                    (await op.GetApiOperationPolicies().GetAsync(PolicyName.Policy, PolicyExportFormat.RawXml, ct)).Value.Data.Value);
                opModel.OriginalRewriteUri = PolicyHelper.GetValue(PolicyHelper.RewriteUri, opModel.PolicyXml);
                opModel.OriginalBackendBaseUrl = PolicyHelper.GetValue(PolicyHelper.BackendBaseUrl, opModel.PolicyXml);
                model.Operations.Add(opModel);
            }

            model.AttachOperations();
            model.ResetTargets();
            result.Add(model);
        }
        log.Report($"Discovery complete: {result.Count} API(s).");
        return result.OrderBy(a => a.DisplayName).ToList();
    }

    /// <summary>Marks APIs and operations that already exist on the destination APIM.</summary>
    public async Task CheckDestinationAsync(ApiManagementServiceResource target, IEnumerable<ApiModel> apis, IProgress<string> log, CancellationToken ct)
    {
        foreach (var api in apis)
        {
            log.Report($"Checking destination for API '{api.DisplayName}'...");
            ApiResource? existing = await TryGetAsync(async () => (await target.GetApis().GetAsync(api.Name, ct)).Value);
            api.ExistsOnDestination = existing is not null;

            var existingOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing is not null)
            {
                await foreach (var op in existing.GetApiOperations().GetAllAsync(cancellationToken: ct))
                {
                    existingOps.Add(op.Data.Name);
                    existingOps.Add($"{op.Data.Method} {op.Data.UriTemplate}");
                }
            }
            foreach (var op in api.Operations)
                op.ExistsOnDestination = existingOps.Contains(op.Name) || existingOps.Contains($"{op.Method} {op.SourceUrlTemplate}");
        }
        log.Report("Destination check complete.");
    }

    // ---------------------------------------------------------------- Migration

    public async Task MigrateAsync(ApiManagementServiceResource source, ApiManagementServiceResource target,
        IReadOnlyList<ApiModel> apis, MigrationOptions options, IProgress<string> log, CancellationToken ct)
    {
        int ok = 0, failed = 0;

        if (options.IncludePolicies)
            await MigratePolicyDependenciesAsync(source, target, apis, log, ct);

        foreach (var api in apis)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await MigrateApiAsync(target, api, options, log, ct);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log.Report($"  ERROR migrating API '{api.DisplayName}': {ex.Message}");
            }
        }
        log.Report($"Migration finished. APIs succeeded: {ok}, failed: {failed}.");
    }

    private async Task MigrateApiAsync(ApiManagementServiceResource target, ApiModel api, MigrationOptions options,
        IProgress<string> log, CancellationToken ct)
    {
        log.Report($"API '{api.DisplayName}'");
        var apis = target.GetApis();
        ApiResource targetApi;
        var updateApiLevel = !api.ExistsOnDestination || options.UpdateExistingApiSettings;

        if (updateApiLevel)
        {
            string? versionSetId = null;
            if (api.VersionSet is not null && api.VersionSetName is not null)
            {
                var vs = await target.GetApiVersionSets().CreateOrUpdateAsync(WaitUntil.Completed, api.VersionSetName,
                    new ApiVersionSetData
                    {
                        DisplayName = api.VersionSet.DisplayName,
                        Description = api.VersionSet.Description,
                        VersioningScheme = api.VersionSet.VersioningScheme,
                        VersionQueryName = api.VersionSet.VersionQueryName,
                        VersionHeaderName = api.VersionSet.VersionHeaderName
                    }, cancellationToken: ct);
                versionSetId = vs.Value.Id.ToString();
                log.Report($"  Version set '{api.VersionSetName}' created/updated.");
            }

            var s = api.Source;
            var content = new ApiCreateOrUpdateContent
            {
                DisplayName = s.DisplayName,
                Description = s.Description,
                Path = api.TargetPath ?? s.Path,
                ServiceUri = string.IsNullOrWhiteSpace(api.TargetServiceUrl) ? null : new Uri(api.TargetServiceUrl),
                ApiType = s.ApiType,
                ApiVersion = s.ApiVersion,
                ApiVersionDescription = s.ApiVersionDescription,
                ApiVersionSetId = versionSetId is null ? null : new ResourceIdentifier(versionSetId),
                IsSubscriptionRequired = s.IsSubscriptionRequired,
                SubscriptionKeyParameterNames = s.SubscriptionKeyParameterNames,
                AuthenticationSettings = s.AuthenticationSettings,
                TermsOfServiceUri = s.TermsOfServiceUri,
                Contact = s.Contact,
                License = s.License
            };
            if (s.Protocols is not null)
                foreach (var p in s.Protocols) content.Protocols.Add(p);

            targetApi = (await apis.CreateOrUpdateAsync(WaitUntil.Completed, api.Name, content, cancellationToken: ct)).Value;
            log.Report($"  API definition {(api.ExistsOnDestination ? "updated" : "created")} (path '/{content.Path}').");

            if (options.IncludeSchemas)
            {
                foreach (var (name, data) in api.Schemas)
                {
                    var schema = new ApiSchemaData { ContentType = data.ContentType, Value = data.Value, Definitions = data.Definitions, Components = data.Components };
                    await targetApi.GetApiSchemas().CreateOrUpdateAsync(WaitUntil.Completed, name, schema, cancellationToken: ct);
                    log.Report($"  Schema '{name}' copied.");
                }
            }
        }
        else
        {
            targetApi = (await apis.GetAsync(api.Name, ct)).Value;
            log.Report("  API already exists on destination - API-level settings and API policy left unchanged.");
        }

        foreach (var op in api.Operations)
        {
            if (!op.IsSelected)
            {
                log.Report($"  Operation {op.Method} {op.SourceUrlTemplate} skipped (not selected){(op.ExistsOnDestination ? " - existing destination operation kept" : "")}.");
                continue;
            }

            var src = op.Source;
            var data = new ApiOperationData
            {
                DisplayName = src.DisplayName,
                Method = src.Method,
                UriTemplate = op.TargetUrlTemplate ?? src.UriTemplate,
                Description = src.Description,
                Request = src.Request,
                Policies = src.Policies
            };
            foreach (var p in src.TemplateParameters) data.TemplateParameters.Add(p);
            foreach (var r in src.Responses) data.Responses.Add(r);

            var createdOp = await targetApi.GetApiOperations().CreateOrUpdateAsync(WaitUntil.Completed, op.Name, data, cancellationToken: ct);
            log.Report($"  Operation {data.Method} {data.UriTemplate} {(op.ExistsOnDestination ? "overwritten" : "created")}.");

            if (options.IncludePolicies && !string.IsNullOrWhiteSpace(op.TargetPolicyXml))
            {
                await createdOp.Value.GetApiOperationPolicies().CreateOrUpdateAsync(WaitUntil.Completed, PolicyName.Policy,
                    new PolicyContractData { Value = op.TargetPolicyXml, Format = PolicyContentFormat.RawXml }, cancellationToken: ct);
                log.Report($"    Operation policy applied{(op.PolicyChanged ? " (modified)" : "")}.");
            }
        }

        if (updateApiLevel && options.IncludePolicies && !string.IsNullOrWhiteSpace(api.TargetApiPolicyXml))
        {
            await targetApi.GetApiPolicies().CreateOrUpdateAsync(WaitUntil.Completed, PolicyName.Policy,
                new PolicyContractData { Value = api.TargetApiPolicyXml, Format = PolicyContentFormat.RawXml }, cancellationToken: ct);
            log.Report($"  API policy applied{(api.ApiPolicyChanged ? " (modified)" : "")}.");
        }
    }

    /// <summary>Copies named values and backends referenced by the selected policies.</summary>
    private async Task MigratePolicyDependenciesAsync(ApiManagementServiceResource source, ApiManagementServiceResource target,
        IReadOnlyList<ApiModel> apis, IProgress<string> log, CancellationToken ct)
    {
        var xmls = apis.SelectMany(a => a.SelectedOperations.Select(o => o.TargetPolicyXml).Append(a.TargetApiPolicyXml))
            .Where(x => x is not null).ToList();
        var namedValues = xmls.SelectMany(PolicyHelper.GetNamedValues).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backends = xmls.SelectMany(PolicyHelper.GetBackendIds).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (namedValues.Count > 0)
        {
            var map = new Dictionary<string, ApiManagementNamedValueResource>(StringComparer.OrdinalIgnoreCase);
            await foreach (var nv in source.GetApiManagementNamedValues().GetAllAsync(cancellationToken: ct))
                map[nv.Data.DisplayName ?? nv.Data.Name] = nv;

            foreach (var key in namedValues)
            {
                if (!map.TryGetValue(key, out var nv)) { log.Report($"WARNING: named value '{key}' not found in source."); continue; }
                try
                {
                    var c = new ApiManagementNamedValueCreateOrUpdateContent
                    {
                        DisplayName = nv.Data.DisplayName,
                        IsSecret = nv.Data.IsSecret
                    };
                    foreach (var t in nv.Data.Tags) c.Tags.Add(t);
                    if (nv.Data.KeyVaultDetails is { } kv)
                        c.KeyVault = new KeyVaultContractCreateProperties { SecretIdentifier = kv.SecretIdentifier, IdentityClientId = kv.IdentityClientId };
                    else
                        c.Value = nv.Data.IsSecret == true ? (await nv.GetValueAsync(ct)).Value.Value : nv.Data.Value;

                    await target.GetApiManagementNamedValues().CreateOrUpdateAsync(WaitUntil.Completed, nv.Data.Name, c, cancellationToken: ct);
                    log.Report($"Named value '{key}' copied.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Report($"WARNING: failed to copy named value '{key}': {ex.Message}");
                }
            }
        }

        foreach (var id in backends)
        {
            try
            {
                var b = await source.GetApiManagementBackends().GetAsync(id, ct);
                var d = b.Value.Data;
                var data = new ApiManagementBackendData
                {
                    Title = d.Title, Description = d.Description, Uri = d.Uri, Protocol = d.Protocol,
                    ResourceUri = d.ResourceUri, Credentials = d.Credentials, Proxy = d.Proxy, Tls = d.Tls
                };
                await target.GetApiManagementBackends().CreateOrUpdateAsync(WaitUntil.Completed, id, data, cancellationToken: ct);
                log.Report($"Backend '{id}' copied.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Report($"WARNING: failed to copy backend '{id}': {ex.Message}");
            }
        }
    }

    private static async Task<TResult?> TryGetAsync<TResult>(Func<Task<TResult>> call)
    {
        try { return await call(); }
        catch (RequestFailedException ex) when (ex.Status == 404) { return default; }
    }
}
