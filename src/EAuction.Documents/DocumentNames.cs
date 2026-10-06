namespace EAuction.Documents;

/// <summary>
/// The uploader's filename, made safe to put in a header.
///
/// Its own class rather than a helper inside <c>Program.cs</c> because it is a
/// security control and a security control deserves a test that calls it directly,
/// not only one that drives it through HTTP — and .NET's own multipart client
/// refuses to send the input that matters, so the HTTP route cannot reach every
/// case on its own.
/// </summary>
public static class DocumentNames
{
    /// <summary>How long a name may be. Long enough for a real one, short enough not to be a header.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Arabic is kept — a booklet is called كراسة الشروط, and renaming it to
    /// <c>document.pdf</c> is not a security measure, it is a worse product.
    ///
    /// What is removed is anything that changes where the name ends: path
    /// separators, control characters, and above all the CR/LF that would let an
    /// uploader append headers of their own to the download response.
    /// </summary>
    public static string Safe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "document";

        // The basename first, so a name like ../../etc/passwd loses its path before
        // anything else looks at it.
        //
        // Split on both separators explicitly rather than with Path.GetFileName,
        // which asks the host OS what a separator is: on Linux a backslash is an
        // ordinary character, so a Windows browser's C:\dir\x.pdf would survive it
        // whole and come out as C:dirx.pdf once the backslashes were stripped. A
        // function whose job is to distrust input should not behave differently
        // depending on which image the pod runs.
        var basename = name.Split('/', '\\')[^1];

        var cleaned = new string(basename
                .Where(c => !char.IsControl(c) && c != '"')
                .ToArray())
            .Trim();

        return cleaned.Length == 0
            ? "document"
            : cleaned[..Math.Min(cleaned.Length, MaxLength)];
    }
}
