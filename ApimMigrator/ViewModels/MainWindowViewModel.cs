using System.Collections.ObjectModel;
using System.Text;
using ApimMigrator.Models;
using ApimMigrator.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApimMigrator.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AzureService _azure = new();
    private CancellationTokenSource? _cts;

    public static readonly string[] PageTitles =
    [
        "Introduction",
        "Connect to Azure",
        "Choose Objects",
        "Migration Options",
        "Destination URLs & Policies",
        "Summary",
        "Migrate"
    ];

    public MainWindowViewModel()
    {
        Pages = new ObservableCollection<PageItem>(PageTitles.Select((t, i) => new PageItem(i, t)));
        UpdatePages();
        Source.SelectionChanged += (_, _) => NextCommand.NotifyCanExecuteChanged();
        Destination.SelectionChanged += (_, _) => NextCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<PageItem> Pages { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle), nameof(IsIntro), nameof(IsConnect), nameof(IsChoose),
        nameof(IsOptions), nameof(IsUrls), nameof(IsSummary), nameof(IsMigrate))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand), nameof(FinishCommand))]
    private int currentPage;

    public string PageTitle => PageTitles[CurrentPage];
    public bool IsIntro => CurrentPage == 0;
    public bool IsConnect => CurrentPage == 1;
    public bool IsChoose => CurrentPage == 2;
    public bool IsOptions => CurrentPage == 3;
    public bool IsUrls => CurrentPage == 4;
    public bool IsSummary => CurrentPage == 5;
    public bool IsMigrate => CurrentPage == 6;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand), nameof(FinishCommand),
        nameof(LoginCommand), nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty] private string status = "Ready.";

    // ------------------------------------------------------------ Connect page

    public ObservableCollection<AzureSession> Sessions { get; } = new();
    [ObservableProperty] private string? loginTenantId;
    [ObservableProperty] private bool useDeviceCode;
    [ObservableProperty] private string? deviceCodeMessage;

    public EndpointSelector Source { get; } = new("Source");
    public EndpointSelector Destination { get; } = new("Destination");

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LoginAsync()
    {
        await RunBusyAsync("Signing in...", async ct =>
        {
            DeviceCodeMessage = null;
            var session = await _azure.LoginAsync(LoginTenantId, UseDeviceCode,
                msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => DeviceCodeMessage = msg), ct);
            Sessions.Add(session);
            Source.Session ??= session;
            Destination.Session ??= session;
            Status = $"Signed in as {session.DisplayName}.";
        });
    }

    // ------------------------------------------------------------ Choose page

    [ObservableProperty] private bool migrateAll = true;
    [ObservableProperty] private ApiRootNode? apiTree;
    public ObservableCollection<ApiRootNode> ApiTreeRoots { get; } = new();
    private string? _discoveredFor;

    partial void OnMigrateAllChanged(bool value)
    {
        if (value && ApiTree is not null) ApiTree.IsChecked = true;
        NextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void SelectAll() { if (ApiTree is not null) ApiTree.IsChecked = true; }
    [RelayCommand] private void DeselectAll() { if (ApiTree is not null) ApiTree.IsChecked = false; }
    [RelayCommand] private void SelectNewOnly() => ApiTree?.SelectNewOnly();

    public IReadOnlyList<ApiModel> SelectedApis =>
        ApiTree is null ? [] : ApiTree.Children.Where(a => a.IsIncluded).ToList();

    public int ConflictCount => ApiTree?.Children.Sum(a => a.Operations.Count(o => o.ExistsOnDestination)) ?? 0;
    public string ConflictText => ConflictCount == 0
        ? "No operations exist on the destination yet."
        : $"{ConflictCount} operation(s) already exist on the destination and are marked [exists on destination]. Untick them to keep the destination version.";

    // ------------------------------------------------------------ Options page

    public MigrationOptions Options { get; } = new();

    public bool IncludePolicies
    {
        get => Options.IncludePolicies;
        set { Options.IncludePolicies = value; OnPropertyChanged(); }
    }
    public bool IncludeSchemas
    {
        get => Options.IncludeSchemas;
        set { Options.IncludeSchemas = value; OnPropertyChanged(); }
    }
    public bool UpdateExistingApiSettings
    {
        get => Options.UpdateExistingApiSettings;
        set { Options.UpdateExistingApiSettings = value; OnPropertyChanged(); }
    }

    // ------------------------------------------------------------ URLs page

    public ObservableCollection<ApiModel> UrlApis { get; } = new();
    [ObservableProperty] private string? findText;
    [ObservableProperty] private string? replaceText;

    [RelayCommand]
    private void ReplaceAll()
    {
        if (string.IsNullOrEmpty(FindText)) return;
        string? R(string? s) => s?.Replace(FindText, ReplaceText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var api in UrlApis)
        {
            api.TargetServiceUrl = R(api.TargetServiceUrl);
            api.TargetApiPolicyXml = R(api.TargetApiPolicyXml);
            foreach (var op in api.SelectedOperations)
            {
                op.TargetUrlTemplate = R(op.TargetUrlTemplate);
                op.TargetPolicyXml = R(op.TargetPolicyXml);
            }
        }
        Status = $"Replaced '{FindText}' in destination URLs and policies.";
    }

    [RelayCommand]
    private void ResetUrls()
    {
        foreach (var api in UrlApis) api.ResetTargets();
    }

    // ------------------------------------------------------------ Summary / migrate

    [ObservableProperty] private string summaryText = string.Empty;
    [ObservableProperty] private string logText = string.Empty;
    [ObservableProperty] private bool migrationDone;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task FinishAsync()
    {
        CurrentPage = 6;
        UpdatePages();
        LogText = string.Empty;
        var progress = new Progress<string>(AppendLog);
        await RunBusyAsync("Migrating...", ct => _azure.MigrateAsync(
            Source.Apim!.Resource, Destination.Apim!.Resource, SelectedApis, Options, progress, ct));
        MigrationDone = true;
        FinishCommand.NotifyCanExecuteChanged();
        Status = "Migration completed. See log for details.";
    }

    private bool CanFinish() => !IsBusy && CurrentPage == 5 && !MigrationDone;

    public string DefaultExportFileName(ExportFormat format) =>
        $"apim-migration-{Destination.Apim?.Resource.Id.Name}-{DateTime.Now:yyyyMMdd-HHmm}.{TemplateExporter.FileExtension(format)}";

    public string BuildExport(ExportFormat format) =>
        TemplateExporter.Export(format, Destination.Apim!, SelectedApis, Options);

    private void AppendLog(string line) => LogText += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";

    // ------------------------------------------------------------ Navigation

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (CurrentPage)
        {
            case 1:
                if (!await EnsureDiscoveredAsync()) return;
                break;
            case 3:
                UrlApis.Clear();
                foreach (var a in SelectedApis) UrlApis.Add(a);
                break;
            case 4:
                SummaryText = BuildSummary();
                break;
        }
        CurrentPage++;
        UpdatePages();
    }

    private bool CanGoNext() => !IsBusy && CurrentPage switch
    {
        1 => Source.Apim is not null && Destination.Apim is not null,
        2 => SelectedApis.Count > 0,
        5 or 6 => false,
        _ => true
    };

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous()
    {
        CurrentPage--;
        UpdatePages();
    }

    private bool CanGoPrevious() => !IsBusy && CurrentPage > 0 && CurrentPage != 6;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    private bool NotBusy() => !IsBusy;

    private async Task<bool> EnsureDiscoveredAsync()
    {
        var key = Source.Apim!.Resource.Id + "|" + Destination.Apim!.Resource.Id;
        if (_discoveredFor == key && ApiTree is not null) return true;

        var progress = new Progress<string>(s => Status = s);
        List<ApiModel>? apis = null;
        await RunBusyAsync("Discovering source APIM...", async ct =>
        {
            apis = await _azure.DiscoverAsync(Source.Apim.Resource, progress, ct);
            await _azure.CheckDestinationAsync(Destination.Apim.Resource, apis, progress, ct);
        });
        if (apis is null) return false;

        ApiTree = new ApiRootNode(apis);
        foreach (var a in apis)
            a.SelectionChanged += (_, _) => NextCommand.NotifyCanExecuteChanged();
        ApiTreeRoots.Clear();
        ApiTreeRoots.Add(ApiTree);
        if (MigrateAll) ApiTree.IsChecked = true;
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(ConflictText));
        _discoveredFor = key;
        return true;
    }

    private string BuildSummary()
    {
        var apis = SelectedApis;
        var sb = new StringBuilder();
        sb.AppendLine("MIGRATION PLAN");
        sb.AppendLine("==============");
        sb.AppendLine($"Source      : {Source.Apim} [{Source.Session}] / {Source.Subscription}");
        sb.AppendLine($"Destination : {Destination.Apim} [{Destination.Session}] / {Destination.Subscription}");
        sb.AppendLine($"Mode        : {(MigrateAll ? "Full migration" : "Selective migration")}");
        sb.AppendLine($"Content     : {(Options.IncludePolicies ? "Definitions + policies (incl. referenced named values and backends)" : "Definitions only")}");
        sb.AppendLine($"Schemas     : {(Options.IncludeSchemas ? "Yes" : "No")}");
        sb.AppendLine($"Existing APIs: {(Options.UpdateExistingApiSettings ? "update API-level settings and API policy" : "keep API-level settings and API policy")}");
        var selOps = apis.SelectMany(a => a.SelectedOperations).ToList();
        sb.AppendLine($"APIs        : {apis.Count}, Operations to migrate: {selOps.Count} " +
                      $"(new: {selOps.Count(o => !o.ExistsOnDestination)}, overwrite: {selOps.Count(o => o.ExistsOnDestination)})");
        sb.AppendLine();

        foreach (var api in apis)
        {
            var apiAction = !api.ExistsOnDestination ? "CREATE" : Options.UpdateExistingApiSettings ? "UPDATE" : "KEEP EXISTING";
            sb.AppendLine($"API: {api.DisplayName} ({api.Name})  [{apiAction}]");
            sb.AppendLine($"  Path        : /{api.SourcePath}{Change(api.SourcePath, api.TargetPath)}");
            sb.AppendLine($"  Service URL : {api.SourceServiceUrl}{Change(api.SourceServiceUrl, api.TargetServiceUrl)}");
            if (api.VersionSetName is not null) sb.AppendLine($"  Version set : {api.VersionSetName}");
            if (Options.IncludeSchemas && api.Schemas.Count > 0) sb.AppendLine($"  Schemas     : {api.Schemas.Count}");
            if (Options.IncludePolicies)
            {
                sb.AppendLine($"  API policy  : {(api.TargetApiPolicyXml is null ? "none" : api.ApiPolicyChanged ? "yes (modified)" : "yes")}");
                if (api.OriginalBackendBaseUrl is not null)
                    sb.AppendLine($"  Backend URL : {api.OriginalBackendBaseUrl}{Change(api.OriginalBackendBaseUrl, api.TargetBackendBaseUrl)}");
            }
            sb.AppendLine("  Operations:");
            foreach (var op in api.Operations)
            {
                var action = !op.IsSelected
                    ? (op.ExistsOnDestination ? "SKIP (keep destination)" : "SKIP")
                    : (op.ExistsOnDestination ? "OVERWRITE" : "CREATE");
                sb.AppendLine($"    [{action}] {op.Method,-7} {op.SourceUrlTemplate}{Change(op.SourceUrlTemplate, op.TargetUrlTemplate)}");
                if (!op.IsSelected || !Options.IncludePolicies || op.TargetPolicyXml is null) continue;

                sb.AppendLine($"        Policy      : {(op.PolicyChanged ? "yes (modified)" : "yes")}");
                if (op.HasRewriteUri)
                    sb.AppendLine($"        Rewrite URI : {op.OriginalRewriteUri}{Change(op.OriginalRewriteUri, op.TargetRewriteUri)}");
                if (op.HasBackendBaseUrl)
                    sb.AppendLine($"        Backend URL : {op.OriginalBackendBaseUrl}{Change(op.OriginalBackendBaseUrl, op.TargetBackendBaseUrl)}");
            }
            sb.AppendLine();
        }
        return sb.ToString();

        static string Change(string? from, string? to) =>
            string.Equals(from ?? "", to ?? "", StringComparison.Ordinal) ? string.Empty : $"  ->  {to}";
    }

    private void UpdatePages()
    {
        foreach (var p in Pages)
        {
            p.IsCurrent = p.Index == CurrentPage;
            p.IsEnabled = p.Index <= CurrentPage;
        }
    }

    private async Task RunBusyAsync(string message, Func<CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Status = message;
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Operation canceled.";
            AppendLog("Operation canceled.");
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    partial void OnCurrentPageChanged(int value) => NextCommand.NotifyCanExecuteChanged();
}

public sealed partial class PageItem(int index, string title) : ObservableObject
{
    public int Index { get; } = index;
    public string Title { get; } = title;
    [ObservableProperty] private bool isCurrent;
    [ObservableProperty] private bool isEnabled;
}
