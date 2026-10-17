using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using PDFtoImage;

// PDFium allocates one contiguous bitmap per render call, so a large-format
// sheet cannot be rasterised in a single pass. A 6741 x 4768 point drawing at
// 300 DPI would need roughly 28000 x 19800 pixels and fails with
// "Unable to allocate pixels for the bitmap."
//
// Large sheets are therefore emitted as a small whole-page preview plus tiles.
// Each tile renders with DpiRelativeToBounds so PDFium only allocates the tile
// itself; without that flag the library rasterises the entire page at the
// requested DPI and crops afterwards, which is what produced the failure.
const long MaxFullBitmapPixels = 32L * 1024L * 1024L;
const int MaxBitmapDimension = 8192;
const int DefaultTileSizePixels = 4096;
const int TileOverlapPixels = 128;
const int PreviewTargetPixels = 1600;
const int AutoGridSize = 3;
const int MaxGridSize = 6;
const int MaxTilesPerPage = 144;

var arguments = ParseArguments(args);
var filePath = arguments.GetValueOrDefault("input");
var outputDirectory = arguments.GetValueOrDefault("output");
var start = ParseInt(arguments, "start", 1);
var end = ParseInt(arguments, "end", start);
var dpi = Math.Clamp(ParseInt(arguments, "dpi", 200), 8, 600);
var grid = Math.Clamp(ParseInt(arguments, "grid", 0), 0, MaxGridSize);
var tileSize = Math.Clamp(
    ParseInt(arguments, "tile-size", DefaultTileSizePixels),
    512,
    DefaultTileSizePixels);
var fullTiles = arguments.ContainsKey("full-tiles");
var region = ParseRegion(arguments.GetValueOrDefault("region"));

if (string.IsNullOrWhiteSpace(filePath)
    || !File.Exists(filePath)
    || string.IsNullOrWhiteSpace(outputDirectory))
{
    WriteResult(false, null, null, null, "Missing input/output path.");
    return 2;
}

try
{
    Directory.CreateDirectory(outputDirectory);
    LoadNativeRuntime();

    var files = new List<object>();
    var errors = new List<object>();
    var pages = new List<object>();
    using var stream = File.OpenRead(filePath);
    var pageSizes = Conversion.GetPageSizes(stream, leaveOpen: true);

    if (region is { } explicitRegion)
    {
        RenderRegion(
            stream,
            filePath,
            outputDirectory,
            start,
            explicitRegion,
            dpi,
            files,
            errors);
    }
    else
    {
        for (var pageNumber = start; pageNumber <= end; pageNumber++)
        {
            var pageIndex = pageNumber - 1;
            if (pageIndex < 0 || pageIndex >= pageSizes.Count)
            {
                continue;
            }

            var pageSize = pageSizes[pageIndex];
            RenderPage(
                stream,
                filePath,
                outputDirectory,
                pageNumber,
                pageSize.Width,
                pageSize.Height,
                dpi,
                grid,
                tileSize,
                fullTiles,
                files,
                errors,
                pages);
        }
    }

    WriteResult(true, files, errors, pages, null);
    return 0;
}
catch (Exception ex)
{
    WriteResult(false, null, null, null, ex.GetBaseException().Message);
    return 3;
}

static void LoadNativeRuntime()
{
    var workerDirectory = AppContext.BaseDirectory;
    var pdfiumPath = Path.Combine(workerDirectory, "pdfium.dll");
    var skiaPath = Path.Combine(workerDirectory, "libSkiaSharp.dll");
    if (!File.Exists(pdfiumPath) || !File.Exists(skiaPath))
    {
        throw new FileNotFoundException(
            $"PDF native runtime is incomplete. pdfium={File.Exists(pdfiumPath)}, "
            + $"skia={File.Exists(skiaPath)}");
    }

    var pdfiumHandle = NativeLibrary.Load(pdfiumPath);
    var skiaHandle = NativeLibrary.Load(skiaPath);
    NativeLibrary.SetDllImportResolver(
        typeof(Conversion).Assembly,
        (libraryName, _, _) =>
        {
            if (libraryName.Equals("pdfium", StringComparison.OrdinalIgnoreCase))
            {
                return pdfiumHandle;
            }

            if (libraryName.Equals(
                    "libSkiaSharp",
                    StringComparison.OrdinalIgnoreCase))
            {
                return skiaHandle;
            }

            return IntPtr.Zero;
        });
}

static void RenderRegion(
    Stream stream,
    string filePath,
    string outputDirectory,
    int pageNumber,
    RectangleF region,
    int dpi,
    List<object> files,
    List<object> errors)
{
    var path = Path.Combine(
        outputDirectory,
        $"{Path.GetFileNameWithoutExtension(filePath)}"
        + $"_page_{pageNumber}_region.png");
    var outcome = TryRender(
        stream,
        path,
        pageNumber - 1,
        region,
        dpi,
        "region",
        errors);
    if (!outcome.Success)
    {
        return;
    }

    files.Add(Describe(
        path,
        pageNumber,
        "region",
        outcome.Dpi,
        region,
        null,
        null));
}

static void RenderPage(
    Stream stream,
    string filePath,
    string outputDirectory,
    int pageNumber,
    float widthPoints,
    float heightPoints,
    int requestedDpi,
    int requestedGrid,
    int tileSize,
    bool fullTiles,
    List<object> files,
    List<object> errors,
    List<object> pages)
{
    if (widthPoints <= 0 || heightPoints <= 0)
    {
        return;
    }

    var fileStem = Path.GetFileNameWithoutExtension(filePath);
    var fullWidth = PointsToPixels(widthPoints, requestedDpi);
    var fullHeight = PointsToPixels(heightPoints, requestedDpi);
    var fitsAtRequestedDpi = fullWidth <= MaxBitmapDimension
        && fullHeight <= MaxBitmapDimension
        && (long)fullWidth * fullHeight <= MaxFullBitmapPixels;

    if (fitsAtRequestedDpi && requestedGrid <= 1)
    {
        var fullPath = Path.Combine(
            outputDirectory,
            $"{fileStem}_page_{pageNumber}.png");
        var outcome = TryRender(
            stream,
            fullPath,
            pageNumber - 1,
            null,
            requestedDpi,
            "page",
            errors);
        if (outcome.Success)
        {
            files.Add(Describe(
                fullPath,
                pageNumber,
                "page",
                outcome.Dpi,
                new RectangleF(0, 0, widthPoints, heightPoints),
                null,
                null));
            pages.Add(new
            {
                page = pageNumber,
                width_points = widthPoints,
                height_points = heightPoints,
                render_mode = "single",
                dpi = outcome.Dpi
            });
            return;
        }
    }

    var previewDpi = Math.Clamp(
        (int)Math.Floor(72.0 * PreviewTargetPixels
            / Math.Max(widthPoints, heightPoints)),
        8,
        requestedDpi);
    var previewPath = Path.Combine(
        outputDirectory,
        $"{fileStem}_page_{pageNumber}_preview.png");
    var previewOutcome = TryRender(
        stream,
        previewPath,
        pageNumber - 1,
        null,
        previewDpi,
        "preview",
        errors);
    if (previewOutcome.Success)
    {
        files.Add(Describe(
            previewPath,
            pageNumber,
            "preview",
            previewOutcome.Dpi,
            new RectangleF(0, 0, widthPoints, heightPoints),
            null,
            null));
    }

    var gridSize = requestedGrid > 0
        ? requestedGrid
        : (fullTiles ? 0 : AutoGridSize);
    var tiles = gridSize > 0
        ? BuildGridTiles(widthPoints, heightPoints, gridSize, tileSize, requestedDpi)
        : BuildFullTiles(widthPoints, heightPoints, requestedDpi, tileSize);

    var rendered = 0;
    foreach (var tile in tiles)
    {
        if (rendered >= MaxTilesPerPage)
        {
            break;
        }

        var suffix = tile.Row.HasValue
            ? $"_tile_r{tile.Row}_c{tile.Column}"
            : $"_tile_{rendered + 1}";
        var tilePath = Path.Combine(
            outputDirectory,
            $"{fileStem}_page_{pageNumber}{suffix}.png");
        var outcome = TryRender(
            stream,
            tilePath,
            pageNumber - 1,
            tile.Bounds,
            tile.Dpi,
            "tile",
            errors);
        if (!outcome.Success)
        {
            continue;
        }

        rendered++;
        files.Add(Describe(
            tilePath,
            pageNumber,
            "tile",
            outcome.Dpi,
            tile.Bounds,
            tile.Row,
            tile.Column));
    }

    pages.Add(new
    {
        page = pageNumber,
        width_points = widthPoints,
        height_points = heightPoints,
        render_mode = "tiled",
        preview_dpi = previewOutcome.Success ? previewOutcome.Dpi : (int?)null,
        grid = gridSize > 0 ? gridSize : (int?)null,
        tile_pixel_size = tileSize,
        tile_count = rendered
    });
}

static List<TilePlan> BuildGridTiles(
    float widthPoints,
    float heightPoints,
    int gridSize,
    int tileSize,
    int requestedDpi)
{
    var cellWidth = widthPoints / gridSize;
    var cellHeight = heightPoints / gridSize;
    var overlap = Math.Max(12.0f, (float)Math.Max(cellWidth, cellHeight) * 0.02f);
    var dpi = Math.Clamp(
        Math.Min(
            requestedDpi,
            (int)Math.Floor(
                72.0 * tileSize
                / (Math.Max(cellWidth, cellHeight) + (2 * overlap)))),
        8,
        requestedDpi);
    var tiles = new List<TilePlan>();
    for (var row = 0; row < gridSize; row++)
    {
        for (var column = 0; column < gridSize; column++)
        {
            var left = Math.Max(0f, (float)(column * cellWidth) - overlap);
            var top = Math.Max(0f, (float)(row * cellHeight) - overlap);
            var right = Math.Min(
                widthPoints,
                (float)((column + 1) * cellWidth) + overlap);
            var bottom = Math.Min(
                heightPoints,
                (float)((row + 1) * cellHeight) + overlap);
            var bounds = new RectangleF(
                left,
                top,
                right - left,
                bottom - top);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            tiles.Add(new TilePlan(bounds, dpi, row + 1, column + 1));
        }
    }

    return tiles;
}

static List<TilePlan> BuildFullTiles(
    float widthPoints,
    float heightPoints,
    int requestedDpi,
    int tileSize)
{
    var tileWidthPoints = tileSize * 72.0 / requestedDpi;
    var tileHeightPoints = tileSize * 72.0 / requestedDpi;
    var overlapPoints = TileOverlapPixels * 72.0 / requestedDpi;
    var columns = Math.Max(
        1,
        (int)Math.Ceiling(widthPoints / tileWidthPoints));
    var rows = Math.Max(
        1,
        (int)Math.Ceiling(heightPoints / tileHeightPoints));
    var tiles = new List<TilePlan>();
    for (var row = 0; row < rows; row++)
    {
        for (var column = 0; column < columns; column++)
        {
            var left = Math.Max(
                0.0,
                column * tileWidthPoints - overlapPoints);
            var top = Math.Max(
                0.0,
                row * tileHeightPoints - overlapPoints);
            var right = Math.Min(
                widthPoints,
                (column + 1) * tileWidthPoints + overlapPoints);
            var bottom = Math.Min(
                heightPoints,
                (row + 1) * tileHeightPoints + overlapPoints);
            var bounds = new RectangleF(
                (float)left,
                (float)top,
                (float)(right - left),
                (float)(bottom - top));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            tiles.Add(new TilePlan(bounds, requestedDpi, row + 1, column + 1));
        }
    }

    return tiles;
}

static RenderOutcome TryRender(
    Stream stream,
    string path,
    int pageIndex,
    RectangleF? bounds,
    int dpi,
    string label,
    List<object> errors)
{
    var attemptDpi = dpi;
    for (var attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            Conversion.SavePng(
                path,
                stream,
                pageIndex,
                leaveOpen: true,
                options: new RenderOptions(
                    Dpi: attemptDpi,
                    WithAnnotations: true,
                    Bounds: bounds,
                    DpiRelativeToBounds: bounds is not null));
            return new RenderOutcome(true, attemptDpi, null);
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            errors.Add(new
            {
                kind = label,
                file = Path.GetFileName(path),
                dpi = attemptDpi,
                error = message
            });

            // Bitmap allocation failures are usually a resolution problem, so
            // retry at a lower DPI before giving up on this file.
            if (!message.Contains("allocate", StringComparison.OrdinalIgnoreCase)
                || attemptDpi <= 16)
            {
                return new RenderOutcome(false, attemptDpi, message);
            }

            attemptDpi = Math.Max(16, attemptDpi / 2);
        }
    }

    return new RenderOutcome(false, attemptDpi, "render failed");
}

static object Describe(
    string path,
    int pageNumber,
    string kind,
    int dpi,
    RectangleF bounds,
    int? row,
    int? column)
{
    var (width, height) = ReadPngSize(path);
    var size = new FileInfo(path).Length;
    return new
    {
        page = pageNumber,
        kind,
        row,
        column,
        dpi,
        width_px = width,
        height_px = height,
        bytes = size,
        region = new
        {
            x = Math.Round(bounds.X, 2),
            y = Math.Round(bounds.Y, 2),
            width = Math.Round(bounds.Width, 2),
            height = Math.Round(bounds.Height, 2)
        },
        file_path = path
    };
}

static (int Width, int Height) ReadPngSize(string path)
{
    try
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 24
            || header[0] != 0x89
            || header[1] != 0x50
            || header[2] != 0x4E
            || header[3] != 0x47)
        {
            return (0, 0);
        }

        return (ReadBigEndianInt32(header[16..]), ReadBigEndianInt32(header[20..]));
    }
    catch
    {
        return (0, 0);
    }
}

static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
{
    return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}

static RectangleF? ParseRegion(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var parts = value.Split(
        [',', ';', ' '],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length != 4)
    {
        return null;
    }

    var numbers = new float[4];
    for (var index = 0; index < 4; index++)
    {
        if (!float.TryParse(
                parts[index],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out numbers[index]))
        {
            return null;
        }
    }

    if (numbers[2] <= 0 || numbers[3] <= 0)
    {
        return null;
    }

    return new RectangleF(numbers[0], numbers[1], numbers[2], numbers[3]);
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = args[index][2..];
        if (index + 1 < args.Length
            && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[name] = args[index + 1];
            index++;
        }
        else
        {
            result[name] = "true";
        }
    }

    return result;
}

static int ParseInt(
    IReadOnlyDictionary<string, string> arguments,
    string name,
    int defaultValue)
{
    return arguments.TryGetValue(name, out var value)
           && int.TryParse(value, out var parsed)
        ? parsed
        : defaultValue;
}

static int PointsToPixels(double points, int dpi)
{
    return Math.Max(1, (int)Math.Ceiling(points * dpi / 72.0));
}

static void WriteResult(
    bool success,
    IReadOnlyList<object>? files,
    IReadOnlyList<object>? errors,
    IReadOnlyList<object>? pages,
    string? error)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success,
        files,
        errors,
        pages,
        error
    }));
}

internal readonly record struct RenderOutcome(
    bool Success,
    int Dpi,
    string? Error);

internal readonly record struct TilePlan(
    RectangleF Bounds,
    int Dpi,
    int? Row,
    int? Column);
