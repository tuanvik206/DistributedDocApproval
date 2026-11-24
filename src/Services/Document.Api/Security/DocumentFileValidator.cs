using System.IO.Compression;
using System.Security.Cryptography;

namespace Document.Api.Security;

public sealed record ValidatedDocumentFile(
    byte[] Content,
    string OriginalFileName,
    string ContentType,
    string Sha256);

public static class DocumentFileValidator
{
    public const long MaximumFileSizeBytes = 10 * 1024 * 1024;

    public static async Task<(ValidatedDocumentFile? File, string? Error)> ValidateAsync(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return (null, "A non-empty PDF or DOCX file is required.");
        }

        if (file.Length > MaximumFileSizeBytes)
        {
            return (null, "The file must not exceed 10 MB.");
        }

        var fileName = SanitizeFileName(file.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not ".pdf" and not ".docx")
        {
            return (null, "Only PDF and DOCX files are supported.");
        }

        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream(checked((int)file.Length));
        await input.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != file.Length)
        {
            return (null, "The uploaded file size did not match its content.");
        }

        string contentType;
        if (extension == ".pdf")
        {
            if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                return (null, "The file content is not a valid PDF header.");
            }

            contentType = "application/pdf";
        }
        else
        {
            if (!IsDocxPackage(bytes))
            {
                return (null, "The file content is not a valid DOCX package.");
            }

            contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        }

        return (new ValidatedDocumentFile(
            bytes,
            fileName,
            contentType,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), null);
    }

    private static string SanitizeFileName(string? suppliedName)
    {
        var normalized = (suppliedName ?? string.Empty).Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var safe = new string(fileName.Where(character => !char.IsControl(character)).Take(255).ToArray()).Trim();
        return safe;
    }

    private static bool IsDocxPackage(byte[] bytes)
    {
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: false);
            return archive.GetEntry("[Content_Types].xml") is not null
                && archive.GetEntry("word/document.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
