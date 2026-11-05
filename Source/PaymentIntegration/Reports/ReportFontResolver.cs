using PdfSharp.Fonts;

namespace PaymentIntegration.Reports;

internal sealed class ReportFontResolver : IFontResolver
{
    private static readonly Lazy<bool> Registration = new(() =>
    {
        GlobalFontSettings.FontResolver = new ReportFontResolver();
        return true;
    });

    public static void EnsureRegistered() => _ = Registration.Value;

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        familyName == "ReportSans" ? new FontResolverInfo(bold ? "DejaVuSans-Bold" : "DejaVuSans", false, italic) : null;

    public byte[] GetFont(string faceName)
    {
        using var stream = typeof(ReportFontResolver).Assembly
            .GetManifestResourceStream($"PaymentIntegration.Reports.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException("The embedded report font is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
