<p align="center">
  <strong>PDF Editor</strong><br>
  <sub>HTML → PDF • PDF temizleme • WPF • Fluent Design • .NET 8</sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/Windows-WPF-0078D4?style=flat-square&logo=windows" alt="WPF">
  <img src="https://img.shields.io/badge/License-AGPL--3.0-blue?style=flat-square" alt="AGPL-3.0">
</p>

---

<br>

<table>
<tr>
<td width="50%" valign="top">

### 🇹🇷 Türkçe

**PDF Editor**, iki aracı tek pencerede birleştiren bir masaüstü uygulamasıdır:

- **HTML → PDF**: HTML dosyalarını PuppeteerSharp (headless Chromium) ile PDF'e çevirir.
- **PDF Temizle**: Herhangi bir PDF'teki watermark'ları, sayfalarda tekrarlanan logoları ve başlık/altbilgi yazılarını bulur; seçtiklerinizi siler ya da yazıyı değiştirir. Orijinal dosya hiçbir zaman değiştirilmez.

#### HTML → PDF

| Özellik | Açıklama |
|--------|----------|
| **Toplu dönüştürme** | Tek dosya veya klasör seçerek tüm `.html` / `.htm` dosyalarını PDF'e çevirir. |
| **Sürükle-bırak** | Dosya veya klasörü pencereye sürükleyip bırakabilirsiniz. |
| **PDF ayarları** | Sayfa boyutu (A4, Letter, A3, A5, Legal), kenar boşluğu (0,5–2 cm), yatay sayfa. |
| **Son kullanılan** | Son seçilen klasörler ve PDF tercihleri saklanır. |
| **Sonuç listesi** | Her dosya için başarı veya hata mesajı gösterilir. |
| **Temizleyicide aç** | Dönüştürülen PDF'ler tek tıkla PDF Temizle sekmesinde açılır. |

#### PDF Temizle

| Özellik | Açıklama |
|--------|----------|
| **Watermark bulma** | PDF watermark açıklamaları, "watermark" olarak etiketlenmiş içerik (Acrobat, Word), adı watermark/filigran olan katmanlar ve sayfaların çoğunda tekrarlanan soluk, eğik ya da büyük yazı/görseller. PDF programlarının göremediği, sayfaya sıradan çizim olarak gömülmüş watermark'lar da bulunur. |
| **Tekrarlanan öğeler** | Sayfaların çoğunda aynı yerde duran logolar ve üst/alt kenardaki başlık, altbilgi, marka yazıları. |
| **Önizleme** | Bulunan öğe sayfada çerçeveyle gösterilir; "Sonra" görünümü sonucu kaydetmeden gösterir. |
| **Yazı değiştirme** | Tekrarlanan yazının yerine aynı konum, boyut ve renkte yeni metin yazılır; sayfadaki herhangi bir yazı da bul/değiştir ile değiştirilebilir. |
| **Klasör modu** | Bir PDF'te yapılan seçimler aynı kaynaktan gelen diğer PDF'lere de uygulanır. |
| **Şifreli PDF** | Şifre sorulur. |

Kesin watermark'lar işaretli gelir; logo ve başlık gibi "olası" öğeleri siz onaylarsınız. Taranmış PDF'lerde watermark sayfa görüntüsünün parçası olduğundan silinemez.

#### Gereksinimler

- **.NET 8**
- **Windows** (WPF destekleyen sürüm)

#### Kurulum ve çalıştırma

```bash
git clone https://github.com/tahirkoca/pdf-editor.git
cd pdf-editor
dotnet restore
dotnet run --project PdfEditor
```

> İlk HTML dönüştürmesinde PuppeteerSharp Chromium indirir (~150 MB); sonraki dönüştürmeler hızlıdır.

Bir PDF doğrudan da açılabilir: `PdfEditor.exe dosya.pdf`

#### Testler

```bash
dotnet test
```

#### Proje yapısı

```
pdf-editor/
├── PdfEditor.sln
├── PdfEditor/            # WPF uygulaması
│   ├── MainWindow.xaml   # İki sekmeli ana pencere
│   ├── Views/            # HtmlToPdfView, CleanerView
│   └── Services/         # AppSettingsService
├── PdfEditor.Core/       # Arayüzden bağımsız motor
│   ├── Html/             # HtmlToPdfService
│   └── Cleaning/         # ContentStream, ContentWalker, Analyzer, PdfCleaner
└── PdfEditor.Tests/      # xUnit testleri
```

</td>
<td width="50%" valign="top">

### 🇬🇧 English

**PDF Editor** is a desktop app that combines two tools in one window:

- **HTML → PDF**: converts HTML files to PDF with PuppeteerSharp (headless Chromium).
- **PDF Clean**: finds watermarks, logos repeated across pages and repeated header/footer text in any PDF, then removes or rewrites what you select. The original file is never modified.

#### HTML → PDF

| Feature | Description |
|--------|-------------|
| **Batch conversion** | Select a single file or folder to convert all `.html` / `.htm` files to PDF. |
| **Drag & drop** | Drop a file or folder onto the window. |
| **PDF settings** | Page size (A4, Letter, A3, A5, Legal), margin (0.5–2 cm), landscape. |
| **Remember paths** | Last used folders and PDF preferences are saved. |
| **Result list** | Per-file success or error message. |
| **Open in cleaner** | Converted PDFs open in the PDF Clean tab with one click. |

#### PDF Clean

| Feature | Description |
|--------|-------------|
| **Watermark detection** | PDF watermark annotations, content tagged as watermark (Acrobat, Word), layers named watermark, and faint, rotated or large text/images repeated on most pages. Watermarks embedded as ordinary drawings, which PDF viewers cannot see as watermarks, are found too. |
| **Repeated items** | Logos at the same spot on most pages and header, footer or brand text in the page margins. |
| **Preview** | The selected item is outlined on the page; the "After" view shows the result before saving. |
| **Text replacement** | Repeated text can be rewritten at the same position, size and colour; any text can be changed with find/replace. |
| **Folder mode** | Selections made on one PDF are applied to other PDFs from the same source. |
| **Encrypted PDFs** | Asks for the password. |

Certain watermarks are pre-selected; "possible" items such as logos and headers need your confirmation. In scanned PDFs the watermark is part of the page image and cannot be removed.

#### Requirements

- **.NET 8**
- **Windows** (WPF-supported version)

#### Setup and run

```bash
git clone https://github.com/tahirkoca/pdf-editor.git
cd pdf-editor
dotnet restore
dotnet run --project PdfEditor
```

> On the first HTML conversion PuppeteerSharp downloads Chromium (~150 MB); subsequent conversions are fast.

A PDF can also be opened directly: `PdfEditor.exe file.pdf`

#### Tests

```bash
dotnet test
```

#### Project structure

```
pdf-editor/
├── PdfEditor.sln
├── PdfEditor/            # WPF app
│   ├── MainWindow.xaml   # Main window with two tabs
│   ├── Views/            # HtmlToPdfView, CleanerView
│   └── Services/         # AppSettingsService
├── PdfEditor.Core/       # UI independent engine
│   ├── Html/             # HtmlToPdfService
│   └── Cleaning/         # ContentStream, ContentWalker, Analyzer, PdfCleaner
└── PdfEditor.Tests/      # xUnit tests
```

</td>
</tr>
</table>

---

<p align="center">
  <sub><strong>Lisans / License</strong> — AGPL-3.0 (PDF işleme MuPDF.NET ile yapılır / PDF processing uses MuPDF.NET)</sub>
</p>
