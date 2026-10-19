using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteHub.Data;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Notes: pasted HTML, Markdown files on disk (export and import), and images (checking and re-encoding).</summary>
public class NoteFilesTests
{
    private static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "rh-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ------------------------------------------------------------------ HTML

    [Fact]
    public void Pasted_html_keeps_only_the_supported_elements()
    {
        var md = NoteHtml.ToMarkdown("""
            <h1 style="color:red">Title</h1><p>Some <b>bold</b> and <i>italic</i> <span style="font-size:40px">text</span>.</p>
            <script>alert(1)</script><style>p{}</style>
            <ul><li>one</li><li>two</li></ul>
            <p><a href="https://example.com">site</a> <a href="javascript:alert(1)">bad</a> <img src="https://tracker.example/x.png" alt="logo"></p>
            <pre class="language-sql">SELECT 1</pre>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
            """);
        Assert.Contains("# Title", md);
        Assert.Contains("**bold**", md);
        Assert.Contains("*italic*", md);
        Assert.Contains("- one", md);
        Assert.Contains("[site](https://example.com)", md);
        Assert.Contains("bad", md);
        Assert.DoesNotContain("javascript", md);
        Assert.DoesNotContain("alert", md);
        Assert.DoesNotContain("tracker", md);
        Assert.Contains("logo", md);
        Assert.Contains("```sql\nSELECT 1\n```", md);
        Assert.Contains("| A | B |", md);
        Assert.Contains("| 1 | 2 |", md);
    }

    [Fact]
    public void Clipboard_html_fragment_and_header_are_consistent()
    {
        var cf = NoteHtml.ClipboardFormat("<p>ä</p>");
        var bytes = Encoding.UTF8.GetBytes(cf);
        int Offset(string name) => int.Parse(System.Text.RegularExpressions.Regex.Match(cf, name + @":(\d+)").Groups[1].Value);
        Assert.Equal("<p>ä</p>", Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
        Assert.Equal("<p>ä</p>", NoteHtml.Fragment(cf));
    }

    // ------------------------------------------------------------------ files

    [Theory]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("   ", "Untitled page")]
    [InlineData("name. ", "name")]
    public void Export_names_are_safe(string title, string expected) => Assert.Equal(expected, NoteFiles.SafeName(title));

    [Fact]
    public void Export_names_are_cut_at_100_characters() => Assert.Equal(100, NoteFiles.SafeName(new string('x', 300)).Length);

    [Fact]
    public void Export_writes_one_file_per_page_with_unique_names_and_relative_images()
    {
        var dir = TempDir();
        try
        {
            var img = new NoteImage("0123456789abcdef0123456789abcdef", "p", "image/png", 1, 1, [1, 2, 3]);
            var page = new ExportPage("Same", $"Text ![shot](note-file:{img.Id}){{width=200}}\n", [img]);
            var (root, count) = NoteFiles.ExportTree(dir, "Book", [("Section", [page, page])]);
            Assert.Equal(2, count);
            Assert.StartsWith(dir, root);
            var files = Directory.GetFiles(Path.Combine(root, "Section"), "*.md").Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(["Same (2).md", "Same.md"], files);
            var text = File.ReadAllText(Path.Combine(root, "Section", "Same.md"));
            Assert.StartsWith("# Same\n\n", text);
            Assert.Contains("![shot](Same.assets/image-1.png)", text);
            Assert.DoesNotContain("width=", text);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(root, "Section", "Same.assets", "image-1.png")));
            // A second export never overwrites the first.
            var (root2, _) = NoteFiles.ExportTree(dir, "Book", [("Section", [page])]);
            Assert.NotEqual(root, root2);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Import_takes_the_first_heading_as_title_and_follows_image_paths_only_inside_the_folder()
    {
        var dir = TempDir();
        var outside = TempDir();
        try
        {
            var png = Sta(() => PngBytes(4, 4));
            File.WriteAllBytes(Path.Combine(dir, "in.png"), png);
            File.WriteAllBytes(Path.Combine(outside, "out.png"), png);
            var md = $"# My page\n\n![a](in.png) ![b](../{Path.GetFileName(outside)}/out.png) ![c]({Path.Combine(outside, "out.png")}) ![d](https://example.com/x.png) ![e](\\\\server\\share\\x.png)\n";
            var file = Path.Combine(dir, "file.md");
            File.WriteAllText(file, md);
            var page = Sta(() => NoteFiles.Import(file));
            Assert.Equal("My page", page.Title);
            Assert.Single(page.Images);
            Assert.Contains($"![a](note-file:{NoteFiles.Pending}0)", page.Body);
            Assert.Contains("out.png", page.Body);         // left as text
            Assert.Contains("https://example.com/x.png", page.Body);
            Assert.DoesNotContain("# My page", page.Body);
        }
        finally { Directory.Delete(dir, true); Directory.Delete(outside, true); }
    }

    [Fact]
    public void Import_refuses_files_over_1_MB()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "big.md");
            File.WriteAllText(file, new string('x', NoteStore.MaxBodyLength + 1));
            Assert.Throws<IOException>(() => NoteFiles.Import(file));
            File.WriteAllText(Path.Combine(dir, "plain.md"), "no heading");
            Assert.Equal("plain", NoteFiles.Import(Path.Combine(dir, "plain.md")).Title);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ images

    private static byte[] PngBytes(int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Images_are_re_encoded_without_metadata()
    {
        const string marker = "Zq9-gps-author-marker";
        var result = Sta(() =>
        {
            var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr24, null, new byte[8 * 8 * 3], 8 * 3);
            var meta = new BitmapMetadata("jpg") { Author = new System.Collections.ObjectModel.ReadOnlyCollection<string>([marker]), Comment = marker };
            var enc = new JpegBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp, null, meta, null));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var source = ms.ToArray();
            Assert.True(source.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0 || source.AsSpan().IndexOf(Encoding.Unicode.GetBytes(marker)) >= 0);
            return NoteImages.Prepare(source);
        });
        Assert.Equal("image/png", result.Mime);
        Assert.Equal(8, result.Width);
        Assert.True(result.Data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) < 0);
        Assert.True(result.Data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(marker)) < 0);
    }

    [Fact]
    public void A_file_that_isnt_an_image_is_refused() =>
        Assert.Throws<NoteImageException>(() => Sta(() => NoteImages.Prepare(Encoding.UTF8.GetBytes("<svg onload=alert(1)/>"))));

    [Fact]
    public void An_image_whose_header_claims_a_huge_size_is_refused()
    {
        // A PNG header that claims 100,000 × 100,000 pixels (40 GB decoded), with no pixel data behind it.
        var ihdr = new List<byte>();
        ihdr.AddRange("IHDR"u8.ToArray());
        ihdr.AddRange(BigEndian(100_000));
        ihdr.AddRange(BigEndian(100_000));
        ihdr.AddRange(new byte[] { 8, 6, 0, 0, 0 });
        var png = new List<byte>(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        png.AddRange(BigEndian(13));
        png.AddRange(ihdr);
        png.AddRange(BigEndian(unchecked((int)Crc32([.. ihdr]))));
        png.AddRange(BigEndian(0));
        png.AddRange("IEND"u8.ToArray());
        png.AddRange(BigEndian(unchecked((int)Crc32("IEND"u8.ToArray()))));
        Assert.Throws<NoteImageException>(() => Sta(() => NoteImages.Prepare([.. png])));
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
