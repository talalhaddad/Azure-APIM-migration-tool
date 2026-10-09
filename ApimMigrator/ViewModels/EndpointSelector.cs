using System.Collections.ObjectModel;
using ApimMigrator.Models;
using ApimMigrator.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApimMigrator.ViewModels;

/// <summary>Session -> Subscription -> APIM cascading selector (independent per side, so tenants/users can differ).</summary>
public sealed partial class EndpointSelector(string title) : ObservableObject
{
    private readonly AzureService _azure = new();

    public string Title { get; } = title;
    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = new();
    public ObservableCollection<ApimItem> Services { get; } = new();

    [ObservableProperty] private AzureSession? session;
    [ObservableProperty] private SubscriptionItem? subscription;
    [ObservableProperty] private ApimItem? apim;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;

    public event EventHandler? SelectionChanged;

    async partial void OnSessionChanged(AzureSession? value)
    {
        Subscriptions.Clear();
        Services.Clear();
        Apim = null;
        if (value is null) return;
        await LoadAsync(async () =>
        {
            foreach (var s in await _azure.GetSubscriptionsAsync(value, CancellationToken.None)) Subscriptions.Add(s);
        });
    }

    async partial void OnSubscriptionChanged(SubscriptionItem? value)
    {
        Services.Clear();
        Apim = null;
        if (value is null || Session is null) return;
        await LoadAsync(async () =>
        {
            foreach (var a in await _azure.GetApimServicesAsync(Session, value, CancellationToken.None)) Services.Add(a);
        });
    }

    partial void OnApimChanged(ApimItem? value) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private async Task LoadAsync(Func<Task> action)
    {
        IsLoading = true;
        Error = null;
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }
}
