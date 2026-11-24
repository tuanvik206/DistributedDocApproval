using System.IO.Compression;
using System.Text;
using Document.Api.Security;
using Microsoft.AspNetCore.Http;

namespace UnitTests;

public sealed class DocumentFileValidatorTests
{
    [Fact]
    public async Task Accepts_pdf_by_content_and_returns_a_safe_download_name()
    {
        var file = CreateFormFile("../../private/approval.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\ncontent"));

        var (validated, error) = await DocumentFileValidator.ValidateAsync(file, TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.NotNull(validated);
        Assert.Equal("approval.pdf", validated.OriginalFileName);
        Assert.Equal("application/pdf", validated.ContentType);
        Assert.Equal(64, validated.Sha256.Length);
    }

    [Fact]
    public async Task Rejects_a_file_with_pdf_extension_but_invalid_content()
    {
        var file = CreateFormFile("approval.pdf", Encoding.ASCII.GetBytes("not a pdf"));

        var (validated, error) = await DocumentFileValidator.ValidateAsync(file, TestContext.Current.CancellationToken);

        Assert.Null(validated);
        Assert.Contains("PDF header", error);
    }

    [Fact]
    public async Task Accepts_docx_only_when_the_required_package_parts_exist()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var contentTypes = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
            {
                contentTypes.Write("<Types />");
            }

            using (var document = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
            {
                document.Write("<document />");
            }
        }

        var file = CreateFormFile("approval.docx", stream.ToArray());
        var (validated, error) = await DocumentFileValidator.ValidateAsync(file, TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.NotNull(validated);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", validated.ContentType);
    }

    [Fact]
    public async Task Rejects_an_unsupported_extension_before_reading_content()
    {
        var file = CreateFormFile("payload.exe", [0x4D, 0x5A]);

        var (validated, error) = await DocumentFileValidator.ValidateAsync(file, TestContext.Current.CancellationToken);

        Assert.Null(validated);
        Assert.Contains("Only PDF and DOCX", error);
    }

    private static FormFile CreateFormFile(string fileName, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.LongLength, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream"
        };
    }
}
