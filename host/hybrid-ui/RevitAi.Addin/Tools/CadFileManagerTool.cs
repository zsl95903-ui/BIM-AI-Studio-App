using System.IO;
using System.Text.Json.Nodes;
using RevitAi.Abstractions.AI;
using RevitAi.Abstractions.Services;
using RevitAi.Core;

namespace RevitAi.Addin.Tools;

[AITool(
    "cad_file_manager",
    Category = "CAD 文件解析",
    Description = "直接解析本地 DWG/DXF 文件，提取图层、线条、文字、图块和图层统计。",
    RequiresTransaction = false,
    RequiresModification = false,
    RequiresActiveDocument = false)]
internal sealed class CadFileManagerTool : IAITool
{
    public string Name => "cad_file_manager";

    public string Category => "CAD 文件解析";

    public string Description => "直接解析本地 DWG/DXF 文件";

    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "operation": {
              "type": "string",
              "enum": ["get_info", "get_layers", "get_lines", "get_texts", "get_blocks", "analyze"]
            },
            "filePath": { "type": "string" },
            "layerName": { "type": "string" },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch", "ft"],
              "default": "mm"
            },
            "maxItems": { "type": "integer", "default": 500 }
          },
          "required": ["operation", "filePath"]
        }
        """;

    public Task<AIToolResult> ExecuteAsync(
        AIToolContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = context.GetParameter("operation", "get_info")
                .Trim()
                .ToLowerInvariant();
            var filePath = context.GetParameter("filePath", string.Empty);
            var unit = context.GetParameter("unit", "mm");
            var maxItems = Math.Clamp(
                context.GetParameter("maxItems", 500),
                1,
                5000);

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Task.FromResult(
                    AIToolResult.Fail($"CAD 文件不存在：{filePath}"));
            }

            var service = CoreServicesFactory.CreateCADFileService()
                ?? throw new InvalidOperationException("无法创建 CAD 文件服务。");
            var data = service.ParseCADFile(filePath, UnitToMillimetres(unit))
                ?? throw new InvalidOperationException("ACadSharp 未能解析该 CAD 文件。");

            object result = operation switch
            {
                "get_info" => new
                {
                    file_path = data.FileInfo.FilePath,
                    layer_count = data.Layers.Count,
                    line_count = data.Lines.Count,
                    text_count = data.Texts.Count,
                    block_count = data.Blocks.Count
                },
                "get_layers" => data.Layers
                    .Take(maxItems)
                    .Select(layer => new
                    {
                        name = layer.Name,
                        color_index = layer.ColorIndex,
                        visible = layer.IsVisible,
                        locked = layer.IsLocked,
                        line_type = layer.LineTypeName
                    })
                    .ToList(),
                "get_lines" => GetLines(data, context, maxItems),
                "get_texts" => GetTexts(data, context, maxItems),
                "get_blocks" => GetBlocks(data, context, maxItems),
                "analyze" => Analyze(data, maxItems),
                _ => throw new InvalidOperationException(
                    $"不支持的 CAD 操作：{operation}")
            };

            return Task.FromResult(AIToolResult.Ok(
                $"CAD 操作完成：{operation}",
                result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                AIToolResult.Fail(ex.GetBaseException().Message));
        }
    }

    private static object GetLines(
        CADFileData data,
        AIToolContext context,
        int maxItems)
    {
        var layerName = context.GetParameter("layerName", string.Empty);
        var lines = string.IsNullOrWhiteSpace(layerName)
            ? data.Lines
            : data.GetLinesByLayer(layerName);

        return lines.Take(maxItems).Select(line => new
        {
            id = line.Id,
            layer = line.LayerName,
            start = new { x = line.StartX, y = line.StartY, z = line.StartZ },
            end = new { x = line.EndX, y = line.EndY, z = line.EndZ },
            length = line.Length
        }).ToList();
    }

    private static object GetTexts(
        CADFileData data,
        AIToolContext context,
        int maxItems)
    {
        var layerName = context.GetParameter("layerName", string.Empty);
        var texts = string.IsNullOrWhiteSpace(layerName)
            ? data.Texts
            : data.GetTextsByLayer(layerName);

        return texts.Take(maxItems).Select(text => new
        {
            id = text.Id,
            layer = text.LayerName,
            content = text.Content,
            x = text.X,
            y = text.Y,
            z = text.Z,
            height = text.Height,
            rotation = text.Rotation,
            block_name = text.BlockName
        }).ToList();
    }

    private static object GetBlocks(
        CADFileData data,
        AIToolContext context,
        int maxItems)
    {
        var layerName = context.GetParameter("layerName", string.Empty);
        var blocks = string.IsNullOrWhiteSpace(layerName)
            ? data.Blocks
            : data.GetBlocksByLayer(layerName);

        return blocks.Take(maxItems).Select(block => new
        {
            layer = block.LayerName,
            name = block.Name,
            x = block.X,
            y = block.Y,
            z = block.Z,
            rotation = block.Rotation,
            scale_x = block.ScaleX,
            scale_y = block.ScaleY,
            scale_z = block.ScaleZ
        }).ToList();
    }

    private static object Analyze(CADFileData data, int maxItems)
    {
        var layers = data.Layers
            .Take(maxItems)
            .Select(layer => new
            {
                name = layer.Name,
                line_count = data.GetLinesByLayer(layer.Name).Count,
                text_count = data.GetTextsByLayer(layer.Name).Count,
                block_count = data.GetBlocksByLayer(layer.Name).Count,
                visible = layer.IsVisible
            })
            .ToList();

        return new
        {
            file_path = data.FileInfo.FilePath,
            total_layers = data.Layers.Count,
            total_lines = data.Lines.Count,
            total_texts = data.Texts.Count,
            total_blocks = data.Blocks.Count,
            layers
        };
    }

    private static double UnitToMillimetres(string unit) =>
        unit.Trim().ToLowerInvariant() switch
        {
            "mm" or "millimeter" or "millimeters" => 1.0,
            "cm" or "centimeter" or "centimeters" => 10.0,
            "m" or "meter" or "meters" => 1000.0,
            "inch" or "inches" => 25.4,
            "ft" or "foot" or "feet" => 304.8,
            _ => 1.0
        };
}

