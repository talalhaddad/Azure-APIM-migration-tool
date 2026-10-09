using ApimMigrator.Services;
using ApimMigrator.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ApimMigrator.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Control { Tag: string tag }) return;
        if (!Enum.TryParse<ExportFormat>(tag, out var format)) return;

        var (typeName, patterns) = format switch
        {
            ExportFormat.Arm => ("ARM template", new[] { "*.json" }),
            ExportFormat.Terraform => ("Terraform", new[] { "*.tf" }),
            _ => ("PowerShell script", new[] { "*.ps1" })
        };

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export {typeName}",
                SuggestedFileName = vm.DefaultExportFileName(format),
                DefaultExtension = TemplateExporter.FileExtension(format),
                FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = patterns }]
            });
            if (file is null) return;

            var content = vm.BuildExport(format);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content);
            vm.Status = $"{typeName} exported to {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Export failed: {ex.Message}";
        }
    }
}
