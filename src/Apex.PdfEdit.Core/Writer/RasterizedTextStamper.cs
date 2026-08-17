using System.Globalization;
using Apex.PdfEdit.Core.Layout;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Renders vector text into a raster image that mimics the appearance of the surrounding
/// scanned document, so an edited region visually matches the source scan instead of
/// standing out as crisp vector text on a scanned background.
///
/// Pipeline: (1) load the same-family TrueType font as an <see cref="SKTypeface"/> via
/// <see cref="SystemFontLocator"/>; (2) render each wrapped line into an
/// <see cref="SKBitmap"/> sized at the source scan's DPI; (3) optionally apply grunge
/// (glyph jitter + rotation) + ink-dropout + speckle passes for typewriter-scan mimicry;
/// (4) convert to a <see cref="PdfImageXObject"/> for the writer to paint.
///
/// <b>Port note</b> — Java uses <c>java.awt.Graphics2D</c> / <c>Font</c> / <c>GlyphVector</c>
/// / <c>BufferedImage</c> / <c>ImageIO</c>; .NET has no AWT so the .NET port uses SkiaSharp
/// (SKBitmap / SKCanvas / SKFont / SKTypeface / SKPath) for pixel-identical output.
/// </summary>
public sealed class RasterizedTextStamper
{
    private readonly ILogger _log;

    /// <summary>
    /// Cache <see cref="SKTypeface"/> instances by (family/weight/forceBold) so multi-line
    /// stamps don't re-load the TTF for each line.
    /// </summary>
    private readonly Dictionary<string, SKTypeface> _typefaceCache = new();

    public RasterizedTextStamper(ILogger<RasterizedTextStamper>? logger = null)
    {
        _log = logger ?? NullLogger<RasterizedTextStamper>.Instance;
    }

    /// <summary>
    /// Rasterise <paramref name="lines"/> into a <see cref="PdfImageXObject"/> sized to
    /// <paramref name="bbox"/> in user-space points at <paramref name="dpi"/> pixels per inch.
    /// </summary>
    public PdfImageXObject? Stamp(Rectangle bbox,
        IReadOnlyList<string> lines,
        float fontSize,
        float lineHeight,
        FontStyle? style,
        float dpi,
        Alignment alignment)
    {
        // Legacy defaults — force-bold + 0.3pt stroke tuned for scan-density.
        return Stamp(bbox, lines, fontSize, lineHeight, style, dpi, alignment,
            forceBold: true, strokeWidthPt: 0.3f);
    }

    /// <summary>
    /// Full-parameter variant. <paramref name="forceBold"/> — when true, load the bold
    /// TTF regardless of style, matching Phase 1's density boost for wholesale rewrites.
    /// <paramref name="strokeWidthPt"/> — outline stroke in points before DPI scaling.
    /// 0.3pt gives typewriter-scan density; 0.15pt just firms the anti-aliased edge for
    /// regular body text; 0 disables the stroke entirely.
    /// </summary>
    public PdfImageXObject? Stamp(Rectangle bbox,
        IReadOnlyList<string> lines,
        float fontSize,
        float lineHeight,
        FontStyle? style,
        float dpi,
        Alignment alignment,
        bool forceBold,
        float strokeWidthPt)
    {
        if (bbox.GetWidth() <= 0 || bbox.GetHeight() <= 0 || lines is null || lines.Count == 0)
        {
            return null;
        }
        float scale = dpi / 72f;
        int widthPx = Math.Max(1, (int)Math.Round(bbox.GetWidth() * scale));
        int heightPx = Math.Max(1, (int)Math.Round(bbox.GetHeight() * scale));

        var typeface = LoadTypeface(style, forceBold);
        var color = SkiaColor(style);

        using var bmp = RenderBitmap(widthPx, heightPx, lines, fontSize, lineHeight,
            typeface, color, scale, alignment, forceBold, strokeWidthPt);
        return EncodePng(bmp);
    }

    /// <summary>
    /// Rasterise using a caller-supplied <see cref="SKTypeface"/> — same layout as
    /// <see cref="Stamp"/> but skips the family lookup so no <see cref="FontStyle"/>-based
    /// resolution can misfire on mangled subset names. Intended for callers that already
    /// have the source's embedded font outlines in hand (via
    /// <see cref="SourcePdfFontResolver.SkiaTypefaceFor"/>).
    /// </summary>
    public PdfImageXObject? StampWithFont(Rectangle bbox,
        IReadOnlyList<string> lines,
        float fontSize,
        float lineHeight,
        SKTypeface? baseFont,
        SKColor color,
        float dpi,
        Alignment alignment,
        float strokeWidthPt)
    {
        if (bbox.GetWidth() <= 0 || bbox.GetHeight() <= 0 || lines is null || lines.Count == 0
            || baseFont is null)
        {
            return null;
        }
        float scale = dpi / 72f;
        int widthPx = Math.Max(1, (int)Math.Round(bbox.GetWidth() * scale));
        int heightPx = Math.Max(1, (int)Math.Round(bbox.GetHeight() * scale));

        using var bmp = RenderBitmap(widthPx, heightPx, lines, fontSize, lineHeight,
            baseFont, color, scale, alignment, forceBold: false, strokeWidthPt: strokeWidthPt);
        return EncodePng(bmp);
    }

    /// <summary>
    /// Convenience for callers that already have the destination canvas: rasterise + paste.
    /// Returns true if a stamp was actually painted.
    /// </summary>
    public bool StampOnto(PdfCanvas canvas, Rectangle bbox,
        IReadOnlyList<string> lines, float fontSize, float lineHeight,
        FontStyle? style, float dpi, Alignment alignment)
    {
        var img = Stamp(bbox, lines, fontSize, lineHeight, style, dpi, alignment);
        if (img is null) return false;
        canvas.AddXObjectFittedIntoRectangle(img, bbox);
        return true;
    }

    /// <summary>
    /// Same as <see cref="StampOnto"/> but exposes the forceBold / strokeWidthPt tuning knobs.
    /// </summary>
    public bool StampOnto(PdfCanvas canvas, Rectangle bbox,
        IReadOnlyList<string> lines, float fontSize, float lineHeight,
        FontStyle? style, float dpi, Alignment alignment,
        bool forceBold, float strokeWidthPt)
    {
        var img = Stamp(bbox, lines, fontSize, lineHeight, style, dpi, alignment,
            forceBold, strokeWidthPt);
        if (img is null) return false;
        canvas.AddXObjectFittedIntoRectangle(img, bbox);
        return true;
    }

    /// <summary>
    /// Direct-typeface variant of <see cref="StampOnto"/>: caller supplies a pre-loaded
    /// <see cref="SKTypeface"/> (typically extracted from the source PDF's embedded
    /// font-program bytes). Bypasses the <see cref="SystemFontLocator"/> family matcher
    /// entirely — the stamp renders with exactly the source's glyph outlines.
    /// </summary>
    public bool StampOntoWithFont(PdfCanvas canvas, Rectangle bbox,
        IReadOnlyList<string> lines, float fontSize, float lineHeight,
        SKTypeface? baseFont, SKColor color,
        float dpi, Alignment alignment, float strokeWidthPt)
    {
        var img = StampWithFont(bbox, lines, fontSize, lineHeight, baseFont, color,
            dpi, alignment, strokeWidthPt);
        if (img is null) return false;
        canvas.AddXObjectFittedIntoRectangle(img, bbox);
        return true;
    }

    /// <summary>
    /// Core render loop — shared by <see cref="Stamp"/> and <see cref="StampWithFont"/>.
    /// Returns a fresh <see cref="SKBitmap"/> owned by the caller (must be disposed).
    /// </summary>
    private static SKBitmap RenderBitmap(int widthPx, int heightPx,
        IReadOnlyList<string> lines, float fontSize, float lineHeight,
        SKTypeface? typeface, SKColor color, float scale,
        Alignment alignment, bool forceBold, float strokeWidthPt)
    {
        var bmp = new SKBitmap(new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);

        // Opaque white background — the image IS the redaction. Anti-aliased text pixels
        // composite against white, yielding solid dark gray at edges (rather than washed-
        // out translucent gray on a transparent alpha channel).
        canvas.Clear(SKColors.White);

        if (typeface is null)
        {
            // No typeface resolved — bitmap stays white (redaction rectangle only).
            return bmp;
        }

        using var font = new SKFont(typeface, fontSize * scale);
        font.Edging = SKFontEdging.SubpixelAntialias;
        font.Subpixel = true;

        using var fill = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        bool stroke = strokeWidthPt > 0f;
        using var strokePaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidthPt * scale,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        // Skia baseline coords are pixel-space (Y-down). Line 1 baseline sits at
        // fontSize * 0.8 from the top; subsequent lines flow downward at lineHeight.
        float baselineYPt = fontSize * 0.8f;
        var align = alignment;

        // Grunge mode: enabled whenever the caller asked for scan density (forceBold=true).
        // Adds small per-glyph position jitter + rotation to break up too-perfect vector
        // kerning that gives crisp edits away against typewriter scan noise.
        var rng = forceBold ? new Random(0xA9EC) : null;
        float jitterMaxPx = forceBold ? Math.Max(0.5f, scale * 0.35f) : 0f;

        foreach (var line in lines)
        {
            if (!string.IsNullOrEmpty(line))
            {
                DrawLine(canvas, line, font, fill, stroke ? strokePaint : null,
                    widthPx, baselineYPt * scale, align, rng, jitterMaxPx);
            }
            baselineYPt += lineHeight;
        }

        if (forceBold && rng is not null)
        {
            PaintInkDropout(bmp, rng);
            PaintSpeckle(bmp, rng, scale);
        }

        return bmp;
    }

    /// <summary>
    /// Draw one line at (<paramref name="startXPx"/>, <paramref name="baselineYPx"/>).
    /// Grunge mode (<paramref name="rng"/> not null) rebuilds the path glyph-by-glyph
    /// with per-glyph jitter + rotation, matching Java's GlyphVector.setGlyphPosition +
    /// setGlyphTransform pattern.
    /// </summary>
    private static void DrawLine(SKCanvas canvas, string line, SKFont font,
        SKPaint fill, SKPaint? strokePaint,
        int widthPx, float baselineYPx, Alignment alignment,
        Random? rng, float jitterMaxPx)
    {
        float lineWidthPx = font.MeasureText(line);
        float startXPx = AlignedStartXPx(widthPx, lineWidthPx, alignment);

        if (rng is null)
        {
            // Fast path — one text blob per line.
            using var blob = SKTextBlob.Create(line, font);
            if (blob is null) return;
            canvas.DrawText(blob, startXPx, baselineYPx, fill);
            if (strokePaint is not null) canvas.DrawText(blob, startXPx, baselineYPx, strokePaint);
            return;
        }

        // Grunge path — per-glyph jitter + rotation. Build a fresh SKPath per glyph,
        // apply an SKMatrix (translate + rotate), then fill/stroke. Advance the cursor
        // by MeasureText on the per-glyph string rather than via GetGlyphWidths — the
        // latter's SkiaSharp 3.x overload picker keeps binding the string variant when
        // fed a ushort[] literal.
        float cursorX = startXPx;
        for (int i = 0; i < line.Length;)
        {
            int cp = char.ConvertToUtf32(line, i);
            int step = char.IsHighSurrogate(line[i]) ? 2 : 1;
            var chStr = char.ConvertFromUtf32(cp);
            float glyphWidth = font.MeasureText(chStr);
            var glyphIds = font.GetGlyphs(chStr);
            if (glyphIds.Length == 0)
            {
                cursorX += glyphWidth;
                i += step;
                continue;
            }
            ushort gid = glyphIds[0];
            using var path = font.GetGlyphPath(gid);
            if (path is null || path.IsEmpty)
            {
                cursorX += glyphWidth;
                i += step;
                continue;
            }
            double dx = (rng.NextDouble() - 0.5) * 2 * jitterMaxPx;
            double dy = (rng.NextDouble() - 0.5) * 2 * jitterMaxPx * 0.6;
            double rotRad = (rng.NextDouble() - 0.5) * 2 * (1.5 * Math.PI / 180.0);

            var xform = SKMatrix.CreateTranslation(cursorX + (float)dx, baselineYPx + (float)dy);
            xform = xform.PreConcat(SKMatrix.CreateRotation((float)rotRad));
            using var transformed = new SKPath(path);
            transformed.Transform(xform);
            canvas.DrawPath(transformed, fill);
            if (strokePaint is not null) canvas.DrawPath(transformed, strokePaint);

            cursorX += glyphWidth;
            i += step;
        }
    }

    /// <summary>
    /// Per-line X offset in image (pixel) space so alignment inside the bbox matches
    /// what the vector layer draws. Negative offsets clamped to 0.
    /// </summary>
    internal static float AlignedStartXPx(int bboxWidthPx, float lineWidthPx, Alignment alignment)
        => alignment switch
        {
            Alignment.Right => Math.Max(0f, bboxWidthPx - lineWidthPx),
            Alignment.Center => Math.Max(0f, (bboxWidthPx - lineWidthPx) / 2f),
            _ => 0f
        };

    /// <summary>
    /// Ink-dropout pass: worn ribbons + dry ink give typewriter glyphs faded interior
    /// patches — small mid-gray specks inside otherwise-dark fills. Runs before the
    /// background speckle pass so drop-out pixels are safe from the background-only guard.
    /// </summary>
    private static void PaintInkDropout(SKBitmap bmp, Random rng)
    {
        int w = bmp.Width, h = bmp.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var px = bmp.GetPixel(x, y);
                int avg = (px.Red + px.Green + px.Blue) / 3;
                if (avg > 60) continue;
                if (rng.Next(100) >= 6) continue;
                byte gray = (byte)(130 + rng.Next(90));
                bmp.SetPixel(x, y, new SKColor(gray, gray, gray));
            }
        }
    }

    private static void PaintSpeckle(SKBitmap bmp, Random rng, float scale)
    {
        int w = bmp.Width, h = bmp.Height;
        // (a) background specks — ~0.35% of pixels darkened to random gray. Skip
        // already-dark pixels — background only.
        int speckCount = Math.Max(60, (int)(w * h * 0.0035));
        for (int i = 0; i < speckCount; i++)
        {
            int x = rng.Next(w), y = rng.Next(h);
            var px = bmp.GetPixel(x, y);
            if ((px.Red + px.Green + px.Blue) / 3 < 200) continue;
            byte gray = (byte)(90 + rng.Next(80));
            bmp.SetPixel(x, y, new SKColor(gray, gray, gray));
        }
        // (b) worn edges — for each dark pixel, small chance to darken a neighbour.
        int step = Math.Max(1, (int)Math.Round(scale * 0.5f));
        for (int y = 2; y < h - 2; y += step)
        {
            for (int x = 2; x < w - 2; x += step)
            {
                var px = bmp.GetPixel(x, y);
                int avg = (px.Red + px.Green + px.Blue) / 3;
                if (avg > 80) continue;
                if (rng.Next(100) >= 22) continue;
                int nx = x + rng.Next(5) - 2;
                int ny = y + rng.Next(5) - 2;
                if (nx == x && ny == y) continue;
                var nPx = bmp.GetPixel(nx, ny);
                int nAvg = (nPx.Red + nPx.Green + nPx.Blue) / 3;
                if (nAvg < 150) continue;
                byte gray = (byte)(60 + rng.Next(90));
                bmp.SetPixel(nx, ny, new SKColor(gray, gray, gray));
            }
        }
    }

    private PdfImageXObject? EncodePng(SKBitmap bmp)
    {
        try
        {
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var bytes = data.ToArray();
            return new PdfImageXObject(ImageDataFactory.Create(bytes));
        }
        catch (Exception e)
        {
            _log.LogWarning("Rasterisation failed: {Msg}", e.Message);
            return null;
        }
    }

    private SKTypeface LoadTypeface(FontStyle? style, bool forceBold)
    {
        var cacheKey = FontCacheKey(style, forceBold);
        if (_typefaceCache.TryGetValue(cacheKey, out var cached)) return cached;

        // Weight strategy — mirrors Java: forceBold=true always loads bold regardless
        // of source weight; forceBold=false respects style.Weight so a regular-source
        // per-word stamp lands as regular vector.
        var effectiveStyle = style;
        if (forceBold && style is not null)
        {
            effectiveStyle = new FontStyle(style.Family, style.Size, "bold",
                style.ColorHex, style.SourceFontObjNumber, style.LeadingRatio);
        }
        SKTypeface? typeface = null;
        var ttfPath = SystemFontLocator.LocateFile(effectiveStyle);
        if (ttfPath is not null)
        {
            try
            {
                typeface = SKTypeface.FromFile(ttfPath);
            }
            catch (Exception e)
            {
                _log.LogDebug("SkiaSharp typeface load from {Path} failed: {Msg}", ttfPath, e.Message);
            }
        }
        typeface ??= SkiaSystemDefault(style, forceBold);
        _typefaceCache[cacheKey] = typeface;
        return typeface;
    }

    /// <summary>
    /// Last-resort typeface when <see cref="SystemFontLocator"/> couldn't find one.
    /// Uses Skia's own family lookup with a serif/mono/sans classification matching
    /// the Java <c>Font.SERIF</c> / <c>Font.MONOSPACED</c> / <c>Font.SANS_SERIF</c> logical fonts.
    /// </summary>
    private static SKTypeface SkiaSystemDefault(FontStyle? style, bool forceBold)
    {
        var familyName = LogicalNameFor(style);
        var weight = forceBold || IsBold(style) ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = SKFontStyleSlant.Upright;
        var typeface = SKTypeface.FromFamilyName(familyName, weight, SKFontStyleWidth.Normal, slant);
        return typeface ?? SKTypeface.Default;
    }

    private static bool IsBold(FontStyle? style)
        => style?.Weight is not null && style.Weight.Equals("bold", StringComparison.OrdinalIgnoreCase);

    private static string FontCacheKey(FontStyle? style, bool forceBold)
    {
        var family = style?.Family ?? "-";
        var weight = style?.Weight ?? "regular";
        return $"{family}/{weight}/{(forceBold ? "B" : "N")}";
    }

    /// <summary>
    /// Mirrors Java's Font.SERIF/MONOSPACED/SANS_SERIF logical fonts via Skia family names.
    /// </summary>
    private static string LogicalNameFor(FontStyle? style)
    {
        if (style?.Family is null) return "sans-serif";
        var family = style.Family.ToLowerInvariant();
        if (family.Contains("times", StringComparison.Ordinal)
            || family.Contains("serif", StringComparison.Ordinal)
            || family.Contains("roman", StringComparison.Ordinal)
            || family.Contains("cambria", StringComparison.Ordinal))
        {
            return "serif";
        }
        if (family.Contains("courier", StringComparison.Ordinal)
            || family.Contains("mono", StringComparison.Ordinal)
            || family.Contains("consolas", StringComparison.Ordinal))
        {
            return "monospace";
        }
        return "sans-serif";
    }

    private static SKColor SkiaColor(FontStyle? style)
    {
        var hex = style?.ColorHex;
        if (hex is null || hex.Length != 7 || hex[0] != '#') return SKColors.Black;
        try
        {
            byte r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new SKColor(r, g, b);
        }
        catch (FormatException)
        {
            return SKColors.Black;
        }
    }
}
