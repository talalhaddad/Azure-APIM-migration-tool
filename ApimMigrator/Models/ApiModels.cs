using System.Collections.ObjectModel;
using ApimMigrator.Services;
using Azure.ResourceManager.ApiManagement;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApimMigrator.Models;

public sealed partial class ApiModel : ObservableObject
{
    private bool _updating;

    public required string Name { get; init; }
    public required ApiData Source { get; init; }

    public string DisplayName => Source.DisplayName ?? Name;
    public string SourcePath => Source.Path ?? string.Empty;
    public string? SourceServiceUrl => Source.ServiceUri?.ToString();

    public string? ApiPolicyXml { get; set; }
    public string? OriginalBackendBaseUrl { get; set; }
    public bool HasBackendBaseUrl => OriginalBackendBaseUrl is not null;
    public bool HasApiPolicy => ApiPolicyXml is not null;
    public List<(string Name, ApiSchemaData Data)> Schemas { get; } = new();
    public string? VersionSetName { get; set; }
    public ApiVersionSetData? VersionSet { get; set; }
    public ObservableCollection<OperationModel> Operations { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TreeLabel))]
    private bool existsOnDestination;

    public string TreeLabel =>
        $"{DisplayName}  (/{SourcePath})  - {Operations.Count} operation(s)" +
        (ExistsOnDestination ? $"  [exists on destination, {Operations.Count(o => o.ExistsOnDestination)} operation(s) already exist]" : "");

    public IEnumerable<OperationModel> SelectedOperations => Operations.Where(o => o.IsSelected);
    public bool HasSelection => Operations.Any(o => o.IsSelected);

    /// <summary>Tri-state: true = all operations, false = none, null = some.</summary>
    public bool? IsChecked
    {
        get
        {
            if (Operations.Count == 0) return _emptySelected;
            if (Operations.All(o => o.IsSelected)) return true;
            if (Operations.All(o => !o.IsSelected)) return false;
            return null;
        }
        set
        {
            var v = value ?? false;
            _updating = true;
            _emptySelected = v;
            foreach (var o in Operations) o.IsSelected = v;
            _updating = false;
            OnPropertyChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private bool _emptySelected;

    /// <summary>True when the API itself is part of the migration (any operation selected, or API without operations ticked).</summary>
    public bool IsIncluded => Operations.Count == 0 ? _emptySelected : HasSelection;

    public event EventHandler? SelectionChanged;

    public void AttachOperations()
    {
        foreach (var op in Operations)
        {
            op.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(OperationModel.IsSelected) || _updating) return;
                OnPropertyChanged(nameof(IsChecked));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
        }
    }

    [ObservableProperty] private string? targetPath;
    [ObservableProperty] private string? targetServiceUrl;
    [ObservableProperty] private string? targetBackendBaseUrl;
    [ObservableProperty] private string? targetApiPolicyXml;

    partial void OnTargetBackendBaseUrlChanged(string? value) =>
        TargetApiPolicyXml = PolicyHelper.SetValue(PolicyHelper.BackendBaseUrl, TargetApiPolicyXml, value);

    partial void OnTargetApiPolicyXmlChanged(string? value)
    {
        TargetBackendBaseUrl = PolicyHelper.GetValue(PolicyHelper.BackendBaseUrl, value);
        OnPropertyChanged(nameof(ApiPolicyChanged));
    }

    public bool ApiPolicyChanged => !string.Equals(ApiPolicyXml, TargetApiPolicyXml, StringComparison.Ordinal);

    public void ResetTargets()
    {
        TargetPath = Source.Path;
        TargetServiceUrl = SourceServiceUrl;
        TargetApiPolicyXml = ApiPolicyXml;
        TargetBackendBaseUrl = OriginalBackendBaseUrl;
        foreach (var op in Operations) op.ResetTargets();
    }
}

public sealed partial class OperationModel : ObservableObject
{
    public required string Name { get; init; }
    public required ApiOperationData Source { get; init; }

    public string DisplayName => Source.DisplayName ?? Name;
    public string Method => Source.Method ?? string.Empty;
    public string? SourceUrlTemplate => Source.UriTemplate;

    public string? PolicyXml { get; set; }
    public string? OriginalRewriteUri { get; set; }
    public string? OriginalBackendBaseUrl { get; set; }
    public bool HasPolicy => PolicyXml is not null;
    public bool HasRewriteUri => OriginalRewriteUri is not null;
    public bool HasBackendBaseUrl => OriginalBackendBaseUrl is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TreeLabel))]
    private bool existsOnDestination;

    [ObservableProperty] private bool isSelected;

    public string TreeLabel =>
        $"{Method,-7} {Source.UriTemplate}   ({DisplayName})" +
        (HasPolicy ? "  [policy]" : "") +
        (ExistsOnDestination ? "  [exists on destination]" : "");

    [ObservableProperty] private string? targetUrlTemplate;
    [ObservableProperty] private string? targetRewriteUri;
    [ObservableProperty] private string? targetBackendBaseUrl;
    [ObservableProperty] private string? targetPolicyXml;

    partial void OnTargetRewriteUriChanged(string? value) =>
        TargetPolicyXml = PolicyHelper.SetValue(PolicyHelper.RewriteUri, TargetPolicyXml, value);

    partial void OnTargetBackendBaseUrlChanged(string? value) =>
        TargetPolicyXml = PolicyHelper.SetValue(PolicyHelper.BackendBaseUrl, TargetPolicyXml, value);

    partial void OnTargetPolicyXmlChanged(string? value)
    {
        TargetRewriteUri = PolicyHelper.GetValue(PolicyHelper.RewriteUri, value);
        TargetBackendBaseUrl = PolicyHelper.GetValue(PolicyHelper.BackendBaseUrl, value);
        OnPropertyChanged(nameof(PolicyChanged));
    }

    public bool PolicyChanged => !string.Equals(PolicyXml, TargetPolicyXml, StringComparison.Ordinal);

    public void ResetTargets()
    {
        TargetUrlTemplate = Source.UriTemplate;
        TargetPolicyXml = PolicyXml;
        TargetRewriteUri = OriginalRewriteUri;
        TargetBackendBaseUrl = OriginalBackendBaseUrl;
    }
}

/// <summary>Tri-state root node of the selection tree.</summary>
public sealed class ApiRootNode : ObservableObject
{
    public ApiRootNode(IEnumerable<ApiModel> apis)
    {
        Children = new ObservableCollection<ApiModel>(apis);
        foreach (var api in Children)
            api.SelectionChanged += (_, _) => OnPropertyChanged(nameof(IsChecked));
    }

    public ObservableCollection<ApiModel> Children { get; }
    public string Title => $"APIs ({Children.Count})";

    public bool? IsChecked
    {
        get
        {
            if (Children.Count > 0 && Children.All(c => c.IsChecked == true)) return true;
            if (Children.All(c => c.IsChecked == false)) return false;
            return null;
        }
        set
        {
            foreach (var c in Children) c.IsChecked = value == true;
            OnPropertyChanged();
        }
    }

    /// <summary>Selects everything except operations that already exist on the destination.</summary>
    public void SelectNewOnly()
    {
        foreach (var api in Children)
        {
            if (api.Operations.Count == 0) { api.IsChecked = !api.ExistsOnDestination; continue; }
            foreach (var op in api.Operations) op.IsSelected = !op.ExistsOnDestination;
        }
        OnPropertyChanged(nameof(IsChecked));
    }
}
