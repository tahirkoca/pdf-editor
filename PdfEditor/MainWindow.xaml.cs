using System.IO;
using System.Windows;
using Wpf.Ui.Controls;
using PdfEditor.Services;
using PdfEditor.Views;

namespace PdfEditor;

/// <summary>
/// Ana pencere: HTML → PDF dönüştürme ve PDF temizleme sekmeleri.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private const int HtmlTab = 0;
    private const int CleanerTab = 1;

    private AppSettingsService _settings = null!;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettingsService.Load();
        HtmlView.Initialize(_settings);
        CleanerView.Initialize(_settings);
        HtmlView.OpenInCleanerRequested += path =>
        {
            Tabs.SelectedIndex = CleanerTab;
            CleanerView.OpenPath(path);
        };

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0)
            Route(args[0]);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        HtmlView.SaveSettings();
        CleanerView.SaveSettings();
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            e.Effects = System.Windows.DragDropEffects.Copy;
        else
            e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            return;
        var paths = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
        if (paths == null || paths.Length == 0)
            return;
        Route(paths[0]);
        e.Handled = true;
    }

    /// <summary>
    /// Sends a dropped or command line path to the right tab: PDFs to the cleaner, HTML to the
    /// converter. For folders with both, the current tab wins.
    /// </summary>
    private void Route(string path)
    {
        if (File.Exists(path))
        {
            if (CleanerView.IsPdf(path))
                OpenInCleaner(path);
            else if (HtmlToPdfView.IsHtml(path))
                OpenInConverter(path);
            return;
        }
        if (!Directory.Exists(path))
            return;
        var hasPdf = CleanerView.FolderHasPdf(path);
        var hasHtml = Directory.EnumerateFiles(path, "*.htm*").Any(HtmlToPdfView.IsHtml);
        if (hasPdf && (!hasHtml || Tabs.SelectedIndex == CleanerTab))
            OpenInCleaner(path);
        else if (hasHtml)
            OpenInConverter(path);
    }

    private void OpenInCleaner(string path)
    {
        Tabs.SelectedIndex = CleanerTab;
        CleanerView.OpenPath(path);
    }

    private void OpenInConverter(string path)
    {
        Tabs.SelectedIndex = HtmlTab;
        HtmlView.SetSource(path);
    }
}
