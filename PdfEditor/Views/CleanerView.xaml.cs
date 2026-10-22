using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MuPDF.NET;
using PdfEditor.Core.Cleaning;
using PdfEditor.Services;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Matrix = MuPDF.NET.Matrix;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;

namespace PdfEditor.Views;

/// <summary>
/// PDF'lerdeki watermark, tekrarlanan logo ve yazıları bulup temizleyen sekme.
/// </summary>
public partial class CleanerView : UserControl
{
    private const float RenderZoom = 2f;
    private static readonly Brush FocusBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private readonly ObservableCollection<object> _items = new();
    private readonly ObservableCollection<ReplacementItem> _replacements = new();
    private readonly DispatcherTimer _previewTimer;
    private AppSettingsService _settings = new();
    private List<string> _files = new();
    private List<Candidate> _candidates = new();
    private string? _path;
    private string? _password;
    private Document? _doc;
    private Document? _afterDoc;
    private int _page;
    private int _token;
    private int _renderToken;
    private bool _busy;
    private bool _switchingFile;

    public CleanerView()
    {
        InitializeComponent();
        CandidateList.ItemsSource = _items;
        ReplacementList.ItemsSource = _replacements;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += async (_, _) =>
        {
            _previewTimer.Stop();
            await BuildPreviewAsync();
        };
        UpdateButtons();
    }

    public bool IsBusy => _busy;

    private bool ShowAfter => AfterRadio.IsChecked == true;

    private IEnumerable<CandidateItem> CandidateItems => _items.OfType<CandidateItem>();

    public void Initialize(AppSettingsService settings)
    {
        _settings = settings;
        FontTextBox.Text = settings.CleanerFontPath ?? "";
    }

    public void SaveSettings()
    {
        _settings.CleanerFontPath = string.IsNullOrWhiteSpace(FontTextBox.Text) ? null : FontTextBox.Text.Trim();
        _settings.Save();
    }

    public static bool IsPdf(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool FolderHasPdf(string path) =>
        Directory.Exists(path) && Directory.EnumerateFiles(path, "*.pdf").Any();

    // ----- background engine calls ---------------------------------------

    private static Task<T> Engine<T>(Func<T> work) => Task.Run(() =>
    {
        lock (PdfCleaner.Sync)
            return work();
    });

    private static Task Engine(Action work) => Task.Run(() =>
    {
        lock (PdfCleaner.Sync)
            work();
    });

    private async Task RunBusy(string status, Func<Task> body)
    {
        _busy = true;
        StatusText.Text = status;
        BusyBar.Visibility = Visibility.Visible;
        UpdateButtons();
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Hata oluştu.";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyBar.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var ready = _doc != null && !_busy;
        SaveButton.IsEnabled = ready;
        SaveFolderButton.IsEnabled = ready && _files.Count > 1;
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        MessageInfoBar.Title = severity switch
        {
            InfoBarSeverity.Success => "Başarılı",
            InfoBarSeverity.Warning => "Uyarı",
            InfoBarSeverity.Error => "Hata",
            _ => "Bilgi"
        };
        MessageInfoBar.Message = message;
        MessageInfoBar.Severity = severity;
        MessageInfoBar.IsOpen = true;
    }

    // ----- opening -------------------------------------------------------

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PDF dosyaları (*.pdf)|*.pdf",
            Title = "PDF dosyası seçin",
            InitialDirectory = InitialDirectory()
        };
        if (dlg.ShowDialog() == true)
            OpenPath(dlg.FileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "PDF dosyalarının bulunduğu klasörü seçin",
            UseDescriptionForTitle = true,
            InitialDirectory = InitialDirectory()
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            OpenPath(dlg.SelectedPath);
    }

    private string InitialDirectory()
    {
        var last = _settings.LastCleanerPath;
        if (string.IsNullOrEmpty(last))
            return "";
        return Directory.Exists(last) ? last : System.IO.Path.GetDirectoryName(last) ?? "";
    }

    /// <summary>Opens a PDF file or a folder of PDFs.</summary>
    public async void OpenPath(string path)
    {
        if (_busy)
            return;
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.pdf").OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (files.Count == 0)
            {
                ShowMessage("Bu klasörde PDF dosyası yok.", InfoBarSeverity.Warning);
                return;
            }
            _files = files;
            _switchingFile = true;
            FileComboBox.ItemsSource = files.Select(f => System.IO.Path.GetFileName(f)).ToList();
            FileComboBox.SelectedIndex = 0;
            _switchingFile = false;
            FileComboBox.Visibility = Visibility.Visible;
            FileNameText.Text = $"{files.Count} PDF — {path}";
        }
        else
        {
            _files = new List<string> { path };
            FileComboBox.Visibility = Visibility.Collapsed;
            FileNameText.Text = System.IO.Path.GetFileName(path);
        }
        _settings.LastCleanerPath = path;
        await LoadAsync(_files[0]);
    }

    private async void FileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingFile || FileComboBox.SelectedIndex < 0 || FileComboBox.SelectedIndex >= _files.Count)
            return;
        await LoadAsync(_files[FileComboBox.SelectedIndex]);
    }

    private async Task<Document?> OpenWithPasswordAsync(string path)
    {
        while (true)
        {
            try
            {
                var password = _password;
                return await Engine(() => PdfCleaner.Open(path, password));
            }
            catch (PasswordRequiredException)
            {
                var password = PasswordDialog.Ask(Window.GetWindow(this), System.IO.Path.GetFileName(path));
                if (password == null)
                    return null;
                _password = password;
            }
        }
    }

    private async Task LoadAsync(string path)
    {
        if (_busy)
            return;
        Document? doc;
        try
        {
            doc = await OpenWithPasswordAsync(path);
        }
        catch (Exception ex)
        {
            ShowMessage($"PDF açılamadı: {ex.Message}", InfoBarSeverity.Error);
            return;
        }
        if (doc == null)
            return;

        await CloseDocumentsAsync();
        _doc = doc;
        _path = path;
        _page = 0;
        _token++;
        _items.Clear();
        _candidates = new List<Candidate>();
        EmptyText.Visibility = Visibility.Collapsed;
        BeforeRadio.IsChecked = true;
        MessageInfoBar.IsOpen = false;
        await RenderAsync();

        await RunBusy("PDF analiz ediliyor...", async () =>
        {
            var candidates = await Engine(() => Analyzer.Analyze(doc));
            if (!ReferenceEquals(doc, _doc))
                return;
            ShowCandidates(candidates);
        });
    }

    private async Task CloseDocumentsAsync()
    {
        var before = _doc;
        var after = _afterDoc;
        _doc = null;
        _afterDoc = null;
        if (before != null || after != null)
            await Engine(() =>
            {
                before?.Close();
                after?.Close();
            });
    }

    // ----- candidates ----------------------------------------------------

    private void ShowCandidates(List<Candidate> candidates)
    {
        _candidates = candidates;
        _items.Clear();
        foreach (var (group, title) in CandidateGroup.Titles)
        {
            var inGroup = candidates.Where(c => c.Group == group).ToList();
            if (inGroup.Count == 0)
                continue;
            _items.Add(new GroupHeader(title));
            foreach (var cand in inGroup)
                _items.Add(new CandidateItem(cand));
        }
        EmptyText.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var certain = candidates.Count(c => c.Certain);
        StatusText.Text = $"{candidates.Count} öğe bulundu, {certain} tanesi kesin watermark.";
        DrawOverlay();
    }

    private void Candidate_Focus(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CandidateItem item)
            return;
        if (e.OriginalSource is DependencyObject source && FindParent<System.Windows.Controls.TextBox>(source) != null)
            return;
        foreach (var other in CandidateItems)
            other.IsCurrent = ReferenceEquals(other, item);
        if (!item.Candidate.Pages.Contains(_page) && item.Candidate.Pages.Count > 0)
        {
            _page = item.Candidate.Pages[0];
            _ = RenderAsync();
        }
        else
        {
            DrawOverlay();
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = child; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T match)
                return match;
        return null;
    }

    private void Candidate_Changed(object sender, RoutedEventArgs e) => Changed();

    private void Replacement_Changed(object sender, TextChangedEventArgs e) => Changed();

    private void Font_Changed(object sender, TextChangedEventArgs e) => Changed();

    private void Changed()
    {
        var after = _afterDoc;
        _afterDoc = null;
        if (after != null)
            _ = Engine(() => after.Close());
        _token++;
        if (ShowAfter)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
        else
        {
            DrawOverlay();
        }
    }

    private List<Candidate> Snapshot() => _candidates.Select(c =>
    {
        var copy = c.Clone();
        copy.Replacement = c.Replacement.Trim();
        return copy;
    }).ToList();

    private string? FontFile => string.IsNullOrWhiteSpace(FontTextBox.Text) ? null : FontTextBox.Text.Trim();

    // ----- replacements --------------------------------------------------

    private async void AddReplacement_Click(object sender, RoutedEventArgs e)
    {
        var oldText = FindTextBox.Text ?? "";
        var newText = ReplaceTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(oldText) || _doc == null || _busy)
            return;
        _replacements.Add(new ReplacementItem(oldText, newText));
        FindTextBox.Text = "";
        ReplaceTextBox.Text = "";
        var doc = _doc;
        var count = await Engine(() => Enumerable.Range(0, doc.PageCount).Sum(i => Analyzer.FindText(doc[i], oldText).Count));
        StatusText.Text = $"\"{oldText}\" {count} yerde bulundu.";
        Changed();
    }

    private void RemoveReplacement_Click(object sender, RoutedEventArgs e)
    {
        if (ReplacementList.SelectedItem is ReplacementItem item)
        {
            _replacements.Remove(item);
            Changed();
        }
    }

    private void ChooseFont_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Font dosyaları (*.ttf;*.otf)|*.ttf;*.otf|Tüm dosyalar (*.*)|*.*",
            Title = "Font seçin",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts)
        };
        if (dlg.ShowDialog() == true)
            FontTextBox.Text = dlg.FileName;
    }

    private List<(string Old, string New)> Replacements() => _replacements.Select(r => (r.Old, r.New)).ToList();

    // ----- preview -------------------------------------------------------

    private async void View_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _doc == null)
            return;
        if (ShowAfter && _afterDoc == null)
            await BuildPreviewAsync();
        else
            await RenderAsync();
    }

    private async Task BuildPreviewAsync()
    {
        if (_doc == null || _path == null)
            return;
        if (_busy)
        {
            _previewTimer.Start();
            return;
        }
        var token = _token;
        var path = _path;
        var password = _password;
        var candidates = Snapshot();
        var replacements = Replacements();
        var fontFile = FontFile;
        await RenderAsync();
        await RunBusy("Önizleme hazırlanıyor...", async () =>
        {
            var after = await Engine(() =>
            {
                var d = PdfCleaner.Open(path, password);
                try
                {
                    PdfCleaner.Apply(d, candidates, replacements, fontFile);
                    return PdfCleaner.OpenBytes(d.Write());
                }
                finally
                {
                    d.Close();
                }
            });
            if (token != _token || path != _path)
            {
                await Engine(() => after.Close());
                if (ShowAfter)
                    _previewTimer.Start();
                return;
            }
            _afterDoc = after;
            StatusText.Text = "Önizleme hazır.";
        });
        await RenderAsync();
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e) => await GoToAsync(_page - 1);

    private async void NextPage_Click(object sender, RoutedEventArgs e) => await GoToAsync(_page + 1);

    private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            await GoToAsync(_page - 1);
        }
        else if (e.Key == Key.Right)
        {
            e.Handled = true;
            await GoToAsync(_page + 1);
        }
    }

    private async Task GoToAsync(int page)
    {
        if (_doc == null)
            return;
        var count = await Engine(() => _doc?.PageCount ?? 0);
        _page = Math.Clamp(page, 0, Math.Max(0, count - 1));
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        var showAfter = ShowAfter;
        var doc = showAfter && _afterDoc != null ? _afterDoc : _doc;
        if (doc == null)
        {
            PageImage.Source = null;
            Overlay.Children.Clear();
            PageText.Text = "";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        var pno = _page;
        var token = ++_renderToken;
        var (bitmap, width, height, count) = await Engine(() =>
        {
            var page = doc[pno];
            var pix = page.GetPixmap(matrix: new Matrix(RenderZoom, RenderZoom));
            var source = BitmapSource.Create(pix.W, pix.H, 72 * RenderZoom, 72 * RenderZoom,
                PixelFormats.Rgb24, null, pix.SAMPLES, pix.W * pix.N);
            source.Freeze();
            return (source, (double)page.Rect.Width, (double)page.Rect.Height, doc.PageCount);
        });
        if (token != _renderToken)
            return;
        PageGrid.Width = width;
        PageGrid.Height = height;
        PageImage.Source = bitmap;
        PageText.Text = $"Sayfa {pno + 1} / {count}";
        if (showAfter && _afterDoc == null)
            PageText.Text += " (hazırlanıyor)";
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if (ShowAfter || _doc == null)
            return;
        foreach (var item in CandidateItems)
        {
            if (!item.IsCurrent && !item.Selected)
                continue;
            if (!item.Candidate.Boxes.TryGetValue(_page, out var boxes))
                continue;
            foreach (var box in boxes)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(box.Width, 1),
                    Height = Math.Max(box.Height, 1),
                    Stroke = item.IsCurrent ? FocusBrush : SelectedBrush,
                    StrokeThickness = item.IsCurrent ? 1.6 : 0.9,
                    StrokeDashArray = item.IsCurrent ? null : new DoubleCollection { 4, 2 },
                };
                Canvas.SetLeft(rect, box.X0);
                Canvas.SetTop(rect, box.Y0);
                Overlay.Children.Add(rect);
            }
        }
    }

    // ----- saving --------------------------------------------------------

    private string DefaultOutputDirectory()
    {
        if (!string.IsNullOrEmpty(_settings.CleanerOutputPath))
            return _settings.CleanerOutputPath;
        var source = System.IO.Path.GetDirectoryName(_path) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return System.IO.Path.Combine(source, "temiz");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _path == null || _busy)
            return;
        var outDir = DefaultOutputDirectory();
        Directory.CreateDirectory(outDir);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF dosyaları (*.pdf)|*.pdf",
            InitialDirectory = outDir,
            FileName = System.IO.Path.GetFileName(_path),
            DefaultExt = ".pdf"
        };
        if (dlg.ShowDialog() != true)
            return;
        var dst = dlg.FileName;
        if (string.Equals(System.IO.Path.GetFullPath(dst), System.IO.Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage("Orijinal dosyanın üzerine yazılamaz; başka bir ad seçin.", InfoBarSeverity.Warning);
            return;
        }
        _settings.CleanerOutputPath = System.IO.Path.GetDirectoryName(dst);
        var path = _path;
        var password = _password;
        var candidates = Snapshot();
        var replacements = Replacements();
        var fontFile = FontFile;
        await RunBusy("Kaydediliyor...", async () =>
        {
            await Engine(() =>
            {
                var d = PdfCleaner.Open(path, password);
                try
                {
                    PdfCleaner.Apply(d, candidates, replacements, fontFile);
                    PdfCleaner.Save(d, dst);
                }
                finally
                {
                    d.Close();
                }
            });
            StatusText.Text = $"Kaydedildi: {dst}";
            ShowMessage($"Temizlenmiş PDF kaydedildi: {dst}", InfoBarSeverity.Success);
        });
    }

    private async void SaveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _path == null || _busy || _files.Count < 2)
            return;
        var initial = DefaultOutputDirectory();
        Directory.CreateDirectory(initial);
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Temizlenmiş PDF'lerin kaydedileceği klasörü seçin",
            UseDescriptionForTitle = true,
            InitialDirectory = initial
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
            return;
        var outDir = dlg.SelectedPath;
        var sourceDirs = _files.Select(f => System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(f)!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceDirs.Contains(System.IO.Path.GetFullPath(outDir)))
        {
            ShowMessage("Çıktı klasörü, PDF'lerin bulunduğu klasörden farklı olmalı.", InfoBarSeverity.Warning);
            return;
        }
        _settings.CleanerOutputPath = outDir;
        var reference = Snapshot();
        var replacements = Replacements();
        var fontFile = FontFile;
        var files = _files.ToList();
        var current = _path;
        var password = _password;
        await RunBusy("Klasör işleniyor...", async () =>
        {
            var done = 0;
            var failed = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                var src = files[i];
                StatusText.Text = $"{i + 1} / {files.Count}: {System.IO.Path.GetFileName(src)}";
                var dst = System.IO.Path.Combine(outDir, System.IO.Path.GetFileName(src));
                Action<List<Candidate>> choose = src == current
                    ? cands => CopySelection(cands, reference)
                    : cands => PdfCleaner.SelectLike(cands, reference);
                try
                {
                    await Engine(() => PdfCleaner.ProcessFile(src, dst, choose, replacements, fontFile, password));
                    done++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{System.IO.Path.GetFileName(src)}: {ex.Message}");
                }
            }
            StatusText.Text = $"{done} PDF kaydedildi: {outDir}";
            if (failed.Count > 0)
                ShowMessage("Bazı dosyalar işlenemedi: " + string.Join("; ", failed), InfoBarSeverity.Warning);
            else
                ShowMessage($"{done} PDF temizlenip kaydedildi: {outDir}", InfoBarSeverity.Success);
        });
    }

    private static void CopySelection(List<Candidate> candidates, List<Candidate> reference)
    {
        var byId = reference.ToDictionary(c => c.Id);
        foreach (var cand in candidates)
            if (byId.TryGetValue(cand.Id, out var match) && match.Signature == cand.Signature)
            {
                cand.Selected = match.Selected;
                cand.Replacement = match.Replacement;
            }
    }
}
