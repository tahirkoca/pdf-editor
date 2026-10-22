using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace PdfEditor.Core.Html;

/// <summary>
/// Converts HTML files to PDF using headless Chromium (PuppeteerSharp).
/// </summary>
public class HtmlToPdfService
{
    private static PaperFormat GetPaperFormat(string name)
    {
        return name?.ToUpperInvariant() switch
        {
            "LETTER" => PaperFormat.Letter,
            "LEGAL" => PaperFormat.Legal,
            "TABLOID" => PaperFormat.Tabloid,
            "A0" => PaperFormat.A0,
            "A1" => PaperFormat.A1,
            "A2" => PaperFormat.A2,
            "A3" => PaperFormat.A3,
            "A5" => PaperFormat.A5,
            "A6" => PaperFormat.A6,
            _ => PaperFormat.A4
        };
    }

    /// <summary>
    /// Converts the given HTML file(s) to PDF and returns a result with per-file success/error log.
    /// </summary>
    public async Task<ConversionResult> ConvertAsync(
        IReadOnlyList<string> htmlPaths,
        string outputDir,
        PdfConversionOptions? options,
        IProgress<(int current, int total)>? progress,
        CancellationToken cancellationToken)
    {
        var result = new ConversionResult();
        if (htmlPaths.Count == 0)
            return result;

        Directory.CreateDirectory(outputDir);

        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox" }
        }).ConfigureAwait(false);

        var paperFormat = GetPaperFormat(options?.PaperFormat ?? "A4");
        var marginCm = options?.MarginCm ?? 1.0;
        var landscape = options?.Landscape ?? false;
        var marginStr = $"{marginCm}cm";

        for (int i = 0; i < htmlPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((i + 1, htmlPaths.Count));

            var htmlPath = Path.GetFullPath(htmlPaths[i]);
            var fileName = Path.GetFileName(htmlPath);

            if (!File.Exists(htmlPath))
            {
                result.Items.Add(new ConversionLogItem(htmlPath, fileName, false, "Dosya bulunamadı."));
                result.ErrorCount++;
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(htmlPath);
            var pdfPath = Path.Combine(outputDir, baseName + ".pdf");
            var fileUri = new Uri(htmlPath).AbsoluteUri;

            IPage? page = null;
            try
            {
                page = await browser.NewPageAsync().ConfigureAwait(false);
                await page.GoToAsync(fileUri, new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
                    Timeout = 60000
                }).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                await page.PdfAsync(pdfPath, new PdfOptions
                {
                    Format = paperFormat,
                    Landscape = landscape,
                    PrintBackground = true,
                    MarginOptions = new MarginOptions
                    {
                        Top = marginStr,
                        Right = marginStr,
                        Bottom = marginStr,
                        Left = marginStr
                    }
                }).ConfigureAwait(false);

                result.Items.Add(new ConversionLogItem(htmlPath, fileName, true, "Tamamlandı"));
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.Items.Add(new ConversionLogItem(htmlPath, fileName, false, ex.Message));
                result.ErrorCount++;
            }
            finally
            {
                if (page != null)
                    await page.CloseAsync().ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>
    /// Collects HTML file paths from a single file or a directory.
    /// </summary>
    public static IReadOnlyList<string> ResolveHtmlPaths(string path)
    {
        path = Path.GetFullPath(path);
        var list = new List<string>();

        if (File.Exists(path))
        {
            if (string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(path), ".htm", StringComparison.OrdinalIgnoreCase))
                list.Add(path);
            return list;
        }

        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.html", SearchOption.TopDirectoryOnly))
                list.Add(file);
            foreach (var file in Directory.EnumerateFiles(path, "*.htm", SearchOption.TopDirectoryOnly))
                list.Add(file);
            return list;
        }

        return list;
    }
}
