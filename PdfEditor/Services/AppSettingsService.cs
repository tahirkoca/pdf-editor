using System.IO;
using System.Text.Json;
using PdfEditor.Core.Html;

namespace PdfEditor.Services;

/// <summary>
/// Son kullanılan klasörleri, PDF ayarlarını ve temizleyici tercihlerini saklar (AppData).
/// </summary>
public class AppSettingsService
{
    private static string SettingsDir(string app) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), app);

    private static string SettingsPath => Path.Combine(SettingsDir("PdfEditor"), "settings.json");

    private static string LegacySettingsPath => Path.Combine(SettingsDir("ConvertHtml2Pdf"), "settings.json");

    public string? LastHtmlPath { get; set; }
    public string? LastOutputPath { get; set; }
    public PdfConversionOptions PdfOptions { get; set; } = new();
    public string? LastCleanerPath { get; set; }
    public string? CleanerOutputPath { get; set; }
    public string? CleanerFontPath { get; set; }

    public static AppSettingsService Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPath;
            if (!File.Exists(path))
                return new AppSettingsService();

            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            if (dto == null)
                return new AppSettingsService();

            return new AppSettingsService
            {
                LastHtmlPath = dto.LastHtmlPath,
                LastOutputPath = dto.LastOutputPath,
                PdfOptions = new PdfConversionOptions
                {
                    PaperFormat = dto.PaperFormat ?? "A4",
                    MarginCm = dto.MarginCm,
                    Landscape = dto.Landscape
                },
                LastCleanerPath = dto.LastCleanerPath,
                CleanerOutputPath = dto.CleanerOutputPath,
                CleanerFontPath = dto.CleanerFontPath
            };
        }
        catch
        {
            return new AppSettingsService();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var dto = new SettingsDto
            {
                LastHtmlPath = LastHtmlPath,
                LastOutputPath = LastOutputPath,
                PaperFormat = PdfOptions.PaperFormat,
                MarginCm = PdfOptions.MarginCm,
                Landscape = PdfOptions.Landscape,
                LastCleanerPath = LastCleanerPath,
                CleanerOutputPath = CleanerOutputPath,
                CleanerFontPath = CleanerFontPath
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private class SettingsDto
    {
        public string? LastHtmlPath { get; set; }
        public string? LastOutputPath { get; set; }
        public string? PaperFormat { get; set; }
        public double MarginCm { get; set; } = 1.0;
        public bool Landscape { get; set; }
        public string? LastCleanerPath { get; set; }
        public string? CleanerOutputPath { get; set; }
        public string? CleanerFontPath { get; set; }
    }
}
