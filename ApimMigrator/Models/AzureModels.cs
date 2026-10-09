using Azure.ResourceManager;
using Azure.ResourceManager.ApiManagement;
using Azure.ResourceManager.Resources;

namespace ApimMigrator.Models;

/// <summary>A signed-in Azure identity (one per user / tenant login).</summary>
public sealed class AzureSession
{
    public required string DisplayName { get; init; }
    public string? TenantId { get; init; }
    public required ArmClient Client { get; init; }
    public override string ToString() => DisplayName;
}

public sealed record SubscriptionItem(SubscriptionResource Resource)
{
    public override string ToString() => $"{Resource.Data.DisplayName} ({Resource.Data.SubscriptionId})";
}

public sealed record ApimItem(ApiManagementServiceResource Resource, AzureSession Session, SubscriptionItem Subscription)
{
    public override string ToString() => $"{Resource.Data.Name} ({Resource.Data.Location})";
}

public sealed class MigrationOptions
{
    public bool IncludePolicies { get; set; } = true;
    public bool IncludeSchemas { get; set; } = true;
    /// <summary>When an API already exists on the destination, also update its API-level settings and API policy.</summary>
    public bool UpdateExistingApiSettings { get; set; }
}
