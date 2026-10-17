using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using RevitAi.Core;
using RevitAi.Core.Agent;
using RevitAi.Addin.Tools;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RevitAi.Addin.Services;

internal sealed class AttachmentPreprocessor
{
    private const int MaxPdfPages = 30;
    private const int MaxExtractedCharacters = 80000;
    private const int MaxPdfVisionPages = 5;
    private const int MaxPdfVisionImages = 10;
    private const long MaxPdfVisionBytes = 24L * 1024L * 1024L;
    private const int LargeFormatGrid = 3;
    private const double LargeFormatPointThreshold = 1000.0;

    public async Task<IReadOnlyList<AgentAttachment>> PrepareAsync(
        IReadOnlyList<AgentAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var prepared = new List<AgentAttachment>(attachments);
        var visionImages = 0;

        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(attachment.FilePath)
                || !File.Exists(attachment.FilePath))
            {
                continue;
            }

            try
            {
                var extension = Path.GetExtension(attachment.FileName)
                    .ToLowerInvariant();
                if (extension == ".pdf")
                {
                    var extraction = ExtractPdf(attachment.FilePath);
                    attachment.ExtractedText = extraction.Text;
                    if (!extraction.HasText)
                    {
                        var remaining = MaxPdfVisionImages - visionImages;
                        var largeFormat =
                            extraction.LongEdgePoints >= LargeFormatPointThreshold;
                        var images = remaining <= 0
                            ? []
                            : await RenderPdfPagesAsync(
                                attachment,
                                extraction.PageCount,
                                largeFormat ? 300 : 200,
                                largeFormat ? LargeFormatGrid : 0,
                                remaining,
                                cancellationToken);
                        if (images.Count > 0)
                        {
                            visionImages += images.Count;
                            prepared.AddRange(images);
                            attachment.ExtractedText =
                                "PDF 没有文字层，已将页面渲染为图片交给视觉模型读取："
                                + $"{images.Count} 张（1 张整页预览 + 高清分块，"
                                + "分块文件名格式 *_tile_r{行}_c{列}.png，行列从 1 起算）。"
                                + "读取图纸信息时请优先使用分块图片，"
                                + "整页预览只用于判断整体布局。";
                        }
                        else
                        {
                            attachment.ExtractionError =
                                attachment.ExtractionError
                                ?? (remaining <= 0
                                    ? "PDF 无文字层，但本次请求的图片配额已用尽。"
                                    : "PDF 无文字层，且页面图片渲染失败。");
                        }
                    }
                }
                else if (extension is ".dwg" or ".dxf")
                {
                    attachment.ExtractedText = ExtractCad(attachment.FilePath);
                }
            }
            catch (Exception ex)
            {
                attachment.ExtractionError = ex.GetBaseException().Message;
            }
        }

        return prepared;
    }

    private static PdfTextExtraction ExtractPdf(string filePath)
    {
        using var document = PdfDocument.Open(filePath);
        var builder = new StringBuilder();
        var pageCount = Math.Min(document.NumberOfPages, MaxPdfPages);
        var firstPage = document.GetPage(1);
        var longEdgePoints = (double)Math.Max(
            firstPage.Width,
            firstPage.Height);

        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var text = ContentOrderTextExtractor.GetText(page);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.AppendLine($"----- PDF Page {pageNumber} -----");
            builder.AppendLine(text);
            if (builder.Length >= MaxExtractedCharacters)
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            return new PdfTextExtraction(
                "PDF 未提取到文本层，正在尝试通过视觉模型读取页面图片。",
                false,
                document.NumberOfPages,
                longEdgePoints);
        }

        if (builder.Length > MaxExtractedCharacters)
        {
            builder.Length = MaxExtractedCharacters;
        }

        return new PdfTextExtraction(
            builder.ToString(),
            true,
            document.NumberOfPages,
            longEdgePoints);
    }

    private static async Task<List<AgentAttachment>> RenderPdfPagesAsync(
        AgentAttachment source,
        int pageCount,
        int dpi,
        int grid,
        int maxImages,
        CancellationToken cancellationToken)
    {
        var pages = Math.Min(pageCount, MaxPdfVisionPages);
        if (pages <= 0 || string.IsNullOrWhiteSpace(source.FilePath))
        {
            return [];
        }

        // Keep the image budget spread over every page: whole-page previews for
        // all pages are attached before any detail tile.
        var effectiveGrid = grid > 0 && pages > 1 ? Math.Min(grid, 2) : grid;
        var rendered = await PdfRenderWorkerClient.RenderAsync(
            source.FilePath,
            1,
            pages,
            dpi,
            outputDirectory: null,
            cancellationToken,
            effectiveGrid);
        if (rendered["success"]?.GetValue<bool>() != true)
        {
            source.ExtractionError = rendered["errorMessage"]?.GetValue<string>()
                ?? "PDF 页面渲染失败。";
            return [];
        }

        var renderedFiles = (rendered["files"]?.AsArray() ?? [])
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
        var ordered = renderedFiles
            .Where(item => !IsTile(item))
            .Concat(renderedFiles.Where(IsTile))
            .ToList();

        var result = new List<AgentAttachment>();
        long totalBytes = 0;
        foreach (var item in ordered)
        {
            if (result.Count >= maxImages)
            {
                break;
            }

            var filePath = item?["file_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            var kind = item?["kind"]?.GetValue<string>() ?? "page";
            var bytes = File.ReadAllBytes(filePath);
            if (!IsValidPng(bytes))
            {
                continue;
            }

            if (bytes.Length > 16 * 1024 * 1024
                || totalBytes + bytes.Length > MaxPdfVisionBytes)
            {
                continue;
            }

            totalBytes += bytes.Length;
            result.Add(new AgentAttachment
            {
                AttachmentId = $"pdf-{kind}-{Guid.NewGuid():N}",
                FileName = Path.GetFileName(filePath),
                ContentType = "image/png",
                DataUrl = "data:image/png;base64,"
                    + Convert.ToBase64String(bytes),
                Size = bytes.Length,
                FilePath = filePath
            });
        }

        return result;
    }

    private static bool IsTile(JsonNode item)
    {
        return string.Equals(
            item["kind"]?.GetValue<string>(),
            "tile",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCad(string filePath)
    {
        var service = CoreServicesFactory.CreateCADFileService()
            ?? throw new InvalidOperationException("无法创建 CAD 文件服务。");
        var data = service.ParseCADFile(filePath, 1.0)
            ?? throw new InvalidOperationException("ACadSharp 无法解析该 CAD 文件。");

        var builder = new StringBuilder();
        builder.AppendLine($"文件: {filePath}");
        builder.AppendLine($"图层: {data.Layers.Count}");
        builder.AppendLine($"线条: {data.Lines.Count}");
        builder.AppendLine($"文字: {data.Texts.Count}");
        builder.AppendLine($"图块: {data.Blocks.Count}");
        builder.AppendLine("图层统计:");

        foreach (var layer in data.Layers.Take(50))
        {
            builder.AppendLine(
                $"- {layer.Name}: "
                + $"{data.GetLinesByLayer(layer.Name).Count} 条线, "
                + $"{data.GetTextsByLayer(layer.Name).Count} 个文字, "
                + $"{data.GetBlocksByLayer(layer.Name).Count} 个图块");
        }

        builder.AppendLine("文字样例:");
        foreach (var text in data.Texts
                     .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                     .Take(200))
        {
            builder.AppendLine(
                $"- [{text.LayerName}] {text.Content} "
                + $"({text.X:F1}, {text.Y:F1})");
        }

        return builder.ToString();
    }

    private sealed record PdfTextExtraction(
        string Text,
        bool HasText,
        int PageCount,
        double LongEdgePoints);

    private static bool IsValidPng(byte[] bytes)
    {
        if (bytes.Length < 24)
        {
            return false;
        }

        ReadOnlySpan<byte> signature =
        [
            0x89, 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A
        ];
        if (!bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            return false;
        }

        var width = ReadBigEndianInt32(bytes, 16);
        var height = ReadBigEndianInt32(bytes, 20);
        return width >= 100 && height >= 100;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }
}

