using System.Runtime.InteropServices;
using iText.IO.Font;
using iText.Kernel.Font;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Loads the <i>full</i> installed version of a font family from the OS's font
/// directory, so an edit that introduces glyphs missing from the source PDF's
/// embedded subset can still render in the correct typeface. Complements
/// <see cref="PageFontInventory"/>: the inventory carries the source PDF's own
/// (subsetted) fonts, this locator carries the same families as un-subsetted
/// files off disk.
///
/// Search paths and filename tables are per-OS: Windows uses <c>C:\Windows\Fonts</c>;
/// macOS searches <c>/System/Library/Fonts/Supplemental</c>, <c>/System/Library/Fonts</c>,
/// and <c>/Library/Fonts</c>; Linux searches <c>/usr/share/fonts</c> +
/// <c>/usr/local/share/fonts</c> and prefers Liberation (metric-compatible substitute)
/// when a Windows-family font isn't installed.
/// </summary>
public static class SystemFontLocator
{
    private enum Platform { Windows, MacOS, Linux, Other }

    private static readonly Platform Current = DetectPlatform();

    private static Platform DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Platform.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return Platform.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return Platform.Linux;
        return Platform.Other;
    }

    /// <summary>
    /// Ultimate-fallback font that is guaranteed to be embedded — used by writers
    /// when neither the source PDF's embedded subset nor a family-matched system
    /// font can render the new text. Maps the caller's style class (serif / mono /
    /// default) onto whichever OS-installed TrueType file matches.
    /// Returns null only if no candidate file is present on the system.
    /// </summary>
    public static PdfFont? LoadUniversalFallback(FontStyle? style)
    {
        var family = style?.Family ?? string.Empty;
        var weight = style?.Weight ?? "regular";
        var bucket = Classify(style);
        foreach (var p in Candidates(bucket, family, weight))
        {
            var f = TryLoad(p);
            if (f is not null) return f;
        }
        return null;
    }

    /// <summary>
    /// Resolve the on-disk font file path for a given <see cref="FontStyle"/>, mirroring
    /// the same family/weight matching that <see cref="LoadUniversalFallback"/> uses.
    /// Exposed for callers outside iText (notably <c>RasterizedTextStamper</c>).
    /// </summary>
    public static string? LocateFile(FontStyle? style)
    {
        var family = style?.Family ?? string.Empty;
        var weight = style?.Weight ?? "regular";
        var bucket = Classify(style);
        foreach (var p in Candidates(bucket, family, weight))
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Try to load the given family + weight from the OS's font directory as an
    /// IDENTITY_H-encoded embedded font. Returns null if no candidate file is
    /// present or iText can't load it.
    /// </summary>
    public static PdfFont? Load(string? family, string? weight)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        var bucket = ClassifyFamily(family);
        var w = weight ?? "regular";
        foreach (var p in Candidates(bucket, family, w))
        {
            var f = TryLoad(p);
            if (f is not null) return f;
        }
        return null;
    }

    private static PdfFont? TryLoad(string p)
    {
        if (!File.Exists(p)) return null;
        try
        {
            return PdfFontFactory.CreateFont(
                p,
                PdfEncodings.IDENTITY_H,
                PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One of: verdana, times, arial, courier, calibri, cambria. Falls back to arial
    /// for the default class (matches previous behaviour of <see cref="LoadUniversalFallback"/>).
    /// </summary>
    private static string Classify(FontStyle? style)
    {
        var family = (style?.Family ?? string.Empty).ToLowerInvariant();
        if (family.Contains("times", StringComparison.Ordinal)
            || family.Contains("serif", StringComparison.Ordinal)
            || family.Contains("roman", StringComparison.Ordinal)
            || family.Contains("cambria", StringComparison.Ordinal))
        {
            return "times";
        }
        if (family.Contains("courier", StringComparison.Ordinal)
            || family.Contains("mono", StringComparison.Ordinal)
            || family.Contains("consolas", StringComparison.Ordinal))
        {
            return "courier";
        }
        return "arial";
    }

    /// <summary>Family-only classifier for <see cref="Load"/>.</summary>
    private static string ClassifyFamily(string family)
    {
        var stem = PageFontInventory.FamilyStem(family);
        if (stem.Contains("verdana", StringComparison.Ordinal)) return "verdana";
        if (stem.Contains("times", StringComparison.Ordinal)
            || stem.Contains("timesnewroman", StringComparison.Ordinal)) return "times";
        if (stem.Contains("arial", StringComparison.Ordinal)
            || stem.Contains("helvetica", StringComparison.Ordinal)) return "arial";
        if (stem.Contains("courier", StringComparison.Ordinal)
            || stem.Contains("consolas", StringComparison.Ordinal)) return "courier";
        if (stem.Contains("calibri", StringComparison.Ordinal)) return "calibri";
        if (stem.Contains("cambria", StringComparison.Ordinal)) return "cambria";
        return string.Empty;
    }

    /// <summary>
    /// Ordered list of candidate on-disk paths for a family bucket + weight, per OS.
    /// First hit wins. Empty when the bucket isn't in the POC's table or the OS isn't supported.
    /// </summary>
    private static IReadOnlyList<string> Candidates(string bucket, string? family, string? weight)
    {
        if (string.IsNullOrWhiteSpace(bucket)) return Array.Empty<string>();
        var stem = string.IsNullOrEmpty(family) ? string.Empty : PageFontInventory.FamilyStem(family);
        var w = (weight ?? string.Empty).ToLowerInvariant();
        bool bold = string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase)
                    || w.Contains("bold", StringComparison.Ordinal) || w.Contains("black", StringComparison.Ordinal)
                    || stem.Contains("bold", StringComparison.Ordinal) || stem.Contains("black", StringComparison.Ordinal);
        bool italic = w.Contains("italic", StringComparison.Ordinal) || w.Contains("oblique", StringComparison.Ordinal)
                      || stem.Contains("italic", StringComparison.Ordinal) || stem.Contains("oblique", StringComparison.Ordinal);
        return Current switch
        {
            Platform.Windows => WindowsCandidates(bucket, bold, italic),
            Platform.MacOS => MacCandidates(bucket, bold, italic),
            Platform.Linux => LinuxCandidates(bucket, bold, italic),
            _ => Array.Empty<string>()
        };
    }

    private static IReadOnlyList<string> WindowsCandidates(string bucket, bool bold, bool italic)
    {
        var dir = Path.Combine("C:", "Windows", "Fonts");
        return bucket switch
        {
            "verdana" => new[]
            {
                Path.Combine(dir, bold && italic ? "verdanaz.ttf"
                    : bold ? "verdanab.ttf"
                    : italic ? "verdanai.ttf"
                    : "verdana.ttf")
            },
            "times" => new[]
            {
                Path.Combine(dir, bold && italic ? "timesbi.ttf"
                    : bold ? "timesbd.ttf"
                    : italic ? "timesi.ttf"
                    : "times.ttf")
            },
            "arial" => new[]
            {
                Path.Combine(dir, bold && italic ? "arialbi.ttf"
                    : bold ? "arialbd.ttf"
                    : italic ? "ariali.ttf"
                    : "arial.ttf")
            },
            "courier" => new[]
            {
                Path.Combine(dir, bold && italic ? "courbi.ttf"
                    : bold ? "courbd.ttf"
                    : italic ? "couri.ttf"
                    : "cour.ttf")
            },
            "calibri" => new[]
            {
                Path.Combine(dir, bold && italic ? "calibriz.ttf"
                    : bold ? "calibrib.ttf"
                    : italic ? "calibrii.ttf"
                    : "calibri.ttf")
            },
            "cambria" => new[]
            {
                Path.Combine(dir, bold && italic ? "cambriaz.ttf"
                    : bold ? "cambriab.ttf"
                    : italic ? "cambriai.ttf"
                    : "cambria.ttc")
            },
            _ => Array.Empty<string>()
        };
    }

    private static IReadOnlyList<string> MacCandidates(string bucket, bool bold, bool italic)
    {
        const string Supp = "/System/Library/Fonts/Supplemental";
        const string Sys = "/System/Library/Fonts";
        const string Lib = "/Library/Fonts";
        var output = new List<string>();
        switch (bucket)
        {
            case "verdana":
                {
                    var name = bold && italic ? "Verdana Bold Italic.ttf"
                        : bold ? "Verdana Bold.ttf"
                        : italic ? "Verdana Italic.ttf"
                        : "Verdana.ttf";
                    output.Add(Path.Combine(Supp, name));
                    output.Add(Path.Combine(Lib, name));
                    break;
                }
            case "times":
                {
                    var tnr = bold && italic ? "Times New Roman Bold Italic.ttf"
                        : bold ? "Times New Roman Bold.ttf"
                        : italic ? "Times New Roman Italic.ttf"
                        : "Times New Roman.ttf";
                    output.Add(Path.Combine(Supp, tnr));
                    output.Add(Path.Combine(Lib, tnr));
                    output.Add(Path.Combine(Sys, "Times.ttc"));
                    break;
                }
            case "arial":
                {
                    var arial = bold && italic ? "Arial Bold Italic.ttf"
                        : bold ? "Arial Bold.ttf"
                        : italic ? "Arial Italic.ttf"
                        : "Arial.ttf";
                    output.Add(Path.Combine(Supp, arial));
                    output.Add(Path.Combine(Lib, arial));
                    output.Add(Path.Combine(Sys, "Helvetica.ttc"));
                    break;
                }
            case "courier":
                {
                    var cn = bold && italic ? "Courier New Bold Italic.ttf"
                        : bold ? "Courier New Bold.ttf"
                        : italic ? "Courier New Italic.ttf"
                        : "Courier New.ttf";
                    output.Add(Path.Combine(Supp, cn));
                    output.Add(Path.Combine(Lib, cn));
                    output.Add(Path.Combine(Sys, "Courier.ttc"));
                    break;
                }
            case "calibri":
                {
                    var calibri = bold && italic ? "Calibri Bold Italic.ttf"
                        : bold ? "Calibri Bold.ttf"
                        : italic ? "Calibri Italic.ttf"
                        : "Calibri.ttf";
                    output.Add(Path.Combine(Supp, calibri));
                    output.Add(Path.Combine(Lib, calibri));
                    break;
                }
            case "cambria":
                output.Add(Path.Combine(Supp, "Cambria.ttc"));
                output.Add(Path.Combine(Lib, "Cambria.ttc"));
                break;
        }
        return output;
    }

    private static IReadOnlyList<string> LinuxCandidates(string bucket, bool bold, bool italic)
    {
        var output = new List<string>();
        const string Msttc = "/usr/share/fonts/truetype/msttcorefonts";
        const string Liberation = "/usr/share/fonts/truetype/liberation";
        const string Dejavu = "/usr/share/fonts/truetype/dejavu";
        const string LocalLiberation = "/usr/local/share/fonts/liberation";
        switch (bucket)
        {
            case "verdana":
                {
                    var name = bold && italic ? "Verdana_Bold_Italic.ttf"
                        : bold ? "Verdana_Bold.ttf"
                        : italic ? "Verdana_Italic.ttf"
                        : "Verdana.ttf";
                    output.Add(Path.Combine(Msttc, name));
                    output.AddRange(LiberationSans(Liberation, bold, italic));
                    output.AddRange(LiberationSans(LocalLiberation, bold, italic));
                    output.Add(Path.Combine(Dejavu, "DejaVuSans.ttf"));
                    break;
                }
            case "times":
                {
                    var tnr = bold && italic ? "Times_New_Roman_Bold_Italic.ttf"
                        : bold ? "Times_New_Roman_Bold.ttf"
                        : italic ? "Times_New_Roman_Italic.ttf"
                        : "Times_New_Roman.ttf";
                    output.Add(Path.Combine(Msttc, tnr));
                    output.AddRange(LiberationSerif(Liberation, bold, italic));
                    output.AddRange(LiberationSerif(LocalLiberation, bold, italic));
                    output.Add(Path.Combine(Dejavu, "DejaVuSerif.ttf"));
                    break;
                }
            case "arial":
                {
                    var arial = bold && italic ? "Arial_Bold_Italic.ttf"
                        : bold ? "Arial_Bold.ttf"
                        : italic ? "Arial_Italic.ttf"
                        : "Arial.ttf";
                    output.Add(Path.Combine(Msttc, arial));
                    output.AddRange(LiberationSans(Liberation, bold, italic));
                    output.AddRange(LiberationSans(LocalLiberation, bold, italic));
                    output.Add(Path.Combine(Dejavu, "DejaVuSans.ttf"));
                    break;
                }
            case "courier":
                {
                    var cn = bold && italic ? "Courier_New_Bold_Italic.ttf"
                        : bold ? "Courier_New_Bold.ttf"
                        : italic ? "Courier_New_Italic.ttf"
                        : "Courier_New.ttf";
                    output.Add(Path.Combine(Msttc, cn));
                    output.AddRange(LiberationMono(Liberation, bold, italic));
                    output.AddRange(LiberationMono(LocalLiberation, bold, italic));
                    output.Add(Path.Combine(Dejavu, "DejaVuSansMono.ttf"));
                    break;
                }
            case "calibri":
            case "cambria":
                output.AddRange(LiberationSans(Liberation, bold, italic));
                output.AddRange(LiberationSans(LocalLiberation, bold, italic));
                output.Add(Path.Combine(Dejavu, "DejaVuSans.ttf"));
                break;
        }
        return output;
    }

    private static IReadOnlyList<string> LiberationSans(string dir, bool bold, bool italic)
    {
        var name = bold && italic ? "LiberationSans-BoldItalic.ttf"
            : bold ? "LiberationSans-Bold.ttf"
            : italic ? "LiberationSans-Italic.ttf"
            : "LiberationSans-Regular.ttf";
        return new[] { Path.Combine(dir, name) };
    }

    private static IReadOnlyList<string> LiberationSerif(string dir, bool bold, bool italic)
    {
        var name = bold && italic ? "LiberationSerif-BoldItalic.ttf"
            : bold ? "LiberationSerif-Bold.ttf"
            : italic ? "LiberationSerif-Italic.ttf"
            : "LiberationSerif-Regular.ttf";
        return new[] { Path.Combine(dir, name) };
    }

    private static IReadOnlyList<string> LiberationMono(string dir, bool bold, bool italic)
    {
        var name = bold && italic ? "LiberationMono-BoldItalic.ttf"
            : bold ? "LiberationMono-Bold.ttf"
            : italic ? "LiberationMono-Italic.ttf"
            : "LiberationMono-Regular.ttf";
        return new[] { Path.Combine(dir, name) };
    }
}
