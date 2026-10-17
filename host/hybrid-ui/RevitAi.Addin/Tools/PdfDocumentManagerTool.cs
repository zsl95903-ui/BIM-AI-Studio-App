using System.IO;
using System.Text;
using RevitAi.Abstractions.AI;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RevitAi.Addin.Tools;

[AITool(
    "pdf_document_manager",
    Category = "PDF 文件解析",
    Description = "解析本地 PDF：读取页数、提取文字，并将指定页面渲染成 PNG。"
        + "大幅面图纸会输出 1 张整页预览和若干高清分块，"
        + "每块都会返回 region（PDF 点坐标）、行列号和像素尺寸。",
    RequiresTransaction = false,
    RequiresModification = false,
    RequiresActiveDocument = false)]
internal sealed class PdfDocumentManagerTool : IAITool
{
    public string Name => "pdf_document_manager";

    public string Category => "PDF 文件解析";

    public string Description => "解析本地 PDF 文件";

    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "operation": {
              "type": "string",
              "enum": ["get_info", "extract_text", "render_pages", "analyze"]
            },
            "filePath": { "type": "string" },
            "pageStart": { "type": "integer", "default": 1 },
            "pageEnd": { "type": "integer", "default": 5 },
            "dpi": {
              "type": "integer",
              "default": 200,
              "description": "渲染 DPI；大幅面页面按分块渲染，单块位图不超过约 4096 像素"
            },
            "grid": {
              "type": "integer",
              "default": 0,
              "description": "大幅面页码的分块网格边长（0=自动，3 表示 3x3 共 9 块）"
            },
            "region": {
              "type": "string",
              "description": "只渲染页面中的一块区域，格式 x,y,width,height（PDF 点，1/72 英寸）"
            },
            "fullTiles": {
              "type": "boolean",
              "default": false,
              "description": "按请求 DPI 输出全部高清分块（数量较多）"
            },
            "maxChars": { "type": "integer", "default": 60000 },
            "outputDirectory": { "type": "string" }
          },
          "required": ["operation", "filePath"]
        }
        """;

    public async Task<AIToolResult> ExecuteAsync(
        AIToolContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = context.GetParameter("operation", "get_info")
                .Trim()
                .ToLowerInvariant();
            var filePath = context.GetParameter("filePath", string.Empty);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return AIToolResult.Fail(
                    $"PDF 文件不存在：{filePath}");
            }

            using var document = PdfDocument.Open(filePath);
            var start = Math.Clamp(
                context.GetParameter("pageStart", 1),
                1,
                document.NumberOfPages);
            var end = Math.Clamp(
                context.GetParameter("pageEnd", Math.Min(start + 4, document.NumberOfPages)),
                start,
                document.NumberOfPages);

            if (operation == "render_pages")
            {
                var rendered = await PdfRenderWorkerClient.RenderAsync(
                    filePath,
                    start,
                    end,
                    Math.Clamp(context.GetParameter("dpi", 200), 72, 400),
                    context.GetParameter("outputDirectory", string.Empty),
                    cancellationToken,
                    Math.Clamp(context.GetParameter("grid", 0), 0, 6),
                    context.GetParameter("region", string.Empty),
                    context.GetParameter("fullTiles", false));
                if (rendered["success"]?.GetValue<bool>() != true)
                {
                    return AIToolResult.Fail(
                        rendered["errorMessage"]?.GetValue<string>()
                        ?? "PDF 页面渲染失败。");
                }

                return AIToolResult.Ok(
                    "PDF 操作完成：render_pages",
                    rendered);
            }

            object result = operation switch
            {
                "get_info" => new
                {
                    file_path = filePath,
                    page_count = document.NumberOfPages,
                    pdf_version = document.Version.ToString()
                },
                "extract_text" => ExtractText(
                    document,
                    start,
                    end,
                    Math.Clamp(context.GetParameter("maxChars", 60000), 1000, 500000)),
                "analyze" => Analyze(document, start, end),
                _ => throw new InvalidOperationException(
                    $"不支持的 PDF 操作：{operation}")
            };

            return AIToolResult.Ok(
                $"PDF 操作完成：{operation}",
                result);
        }
        catch (Exception ex)
        {
            return AIToolResult.Fail(ex.GetBaseException().Message);
        }
    }

    private static object ExtractText(
        PdfDocument document,
        int start,
        int end,
        int maxChars)
    {
        var builder = new StringBuilder();
        var pages = new List<object>();
        for (var pageNumber = start; pageNumber <= end; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var text = ContentOrderTextExtractor.GetText(page);
            var remaining = maxChars - builder.Length;
            if (remaining <= 0)
            {
                break;
            }

            if (text.Length > remaining)
            {
                text = text[..remaining];
            }

            builder.AppendLine($"----- Page {pageNumber} -----");
            builder.AppendLine(text);
            pages.Add(new
            {
                page = pageNumber,
                text_length = text.Length,
                width_points = page.Width,
                height_points = page.Height
            });
        }

        return new
        {
            page_start = start,
            page_end = end,
            extracted_chars = builder.Length,
            pages,
            text = builder.ToString()
        };
    }

    private static object Analyze(PdfDocument document, int start, int end)
    {
        var pages = new List<object>();
        var anyLikelyScanned = false;
        for (var pageNumber = start; pageNumber <= end; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var text = ContentOrderTextExtractor.GetText(page);
            var likelyScanned = text.Trim().Length < 20;
            anyLikelyScanned |= likelyScanned;
            pages.Add(new
            {
                page = pageNumber,
                text_length = text.Length,
                likely_scanned = likelyScanned,
                width_points = page.Width,
                height_points = page.Height
            });
        }

        return new
        {
            page_count = document.NumberOfPages,
            inspected_page_start = start,
            inspected_page_end = end,
            likely_scanned = anyLikelyScanned,
            pages
        };
    }
}

