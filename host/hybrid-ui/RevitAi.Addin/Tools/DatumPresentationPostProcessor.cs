using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RevitAi.Abstractions.AI;
using RevitAi.Core.AI;
using RevitAi.Revit;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

internal sealed class DatumPresentationPostProcessor
{
    private const double MillimetresPerFoot = 304.8;
    private const double MinimumDatumLengthMm = 30000.0;
    private const double DatumPaddingMm = 1500.0;
    private const double GridPaddingMm = 1500.0;
    private const int MaxGridViews = 8;

    private static readonly Color GridRed = new(255, 0, 0);

    private readonly RevitAdapter _adapter;

    public DatumPresentationPostProcessor(RevitAdapter adapter)
    {
        _adapter = adapter;
    }

    public async Task<JsonObject?> ApplyAsync(
        string toolName,
        AIToolContext context,
        JsonObject payload,
        IReadOnlyCollection<long>? gridIds,
        CancellationToken cancellationToken)
    {
        if (toolName.Equals("create_level", StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyLevelPresentationAsync(
                context,
                payload,
                cancellationToken);
        }

        if (toolName.Equals("create_grid", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals(
                "execute_code",
                StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyGridPresentationAsync(
                context,
                payload,
                gridIds,
                cancellationToken);
        }

        return null;
    }

    public static void NormalizeExecuteCodeArguments(ToolCall call)
    {
        if (!call.Name.Equals("execute_code", StringComparison.OrdinalIgnoreCase)
            || call.Arguments["code"] is not JsonValue codeValue
            || !codeValue.TryGetValue<string>(out var code)
            || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var normalized = code.Replace(
            ".IntegerValue",
            ".Value",
            StringComparison.Ordinal);

        normalized = Regex.Replace(
            normalized,
            @"SetDatumExtentType\s*\(\s*(DatumEnds\.(?:End0|End1))\s*,\s*([^,()]+?)\s*\)",
            "SetDatumExtentType($1, $2, DatumExtentType.ViewSpecific)",
            RegexOptions.CultureInvariant);

        if (!string.Equals(normalized, code, StringComparison.Ordinal))
        {
            call.Arguments["code"] = normalized;
        }
    }

    private async Task<JsonObject> ApplyLevelPresentationAsync(
        AIToolContext context,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var ids = ReadIds(payload, "created_levels", "levelId", "level_id", "id");
        if (ids.Count == 0)
        {
            return NotApplied("没有从 create_level 结果中读取到标高 ID。");
        }

        return await ExecuteInRevitAsync(
            context,
            cancellationToken,
            document =>
            {
                var activeView = document.ActiveView;
                var view = activeView is ViewSection section && !section.IsTemplate
                    ? section
                    : FindElevationView(document);
                if (view is null)
                {
                    return new JsonObject
                    {
                        ["applied"] = false,
                        ["reason"] =
                            "标高是 Revit 基准元素，当前文档没有可用的立面或剖面视图，"
                            + "因此无法显示标高线。"
                    };
                }

                var range = GetHorizontalRange(document, view);
                var lineLengthMm = 0.0;
                var formatted = 0;
                var failures = new JsonArray();

                using var transaction = new Transaction(document, "Revit AI 调整标高显示");
                transaction.Start();
                try
                {
                    foreach (var id in ids)
                    {
                        if (document.GetElement(new ElementId(id)) is not Level level)
                        {
                            failures.Add($"标高 ID {id} 不存在");
                            continue;
                        }

                        try
                        {
                            var curve = CreateHorizontalLine(
                                view,
                                level.Elevation,
                                range,
                                out var lengthMm);

                            level.SetDatumExtentType(
                                DatumEnds.End0,
                                view,
                                DatumExtentType.ViewSpecific);
                            level.SetDatumExtentType(
                                DatumEnds.End1,
                                view,
                                DatumExtentType.ViewSpecific);
                            level.SetCurveInView(
                                DatumExtentType.ViewSpecific,
                                view,
                                curve);
                            level.ShowBubbleInView(DatumEnds.End0, view);
                            level.ShowBubbleInView(DatumEnds.End1, view);

                            formatted++;
                            lineLengthMm = lengthMm;
                        }
                        catch (Exception ex)
                        {
                            failures.Add(
                                $"标高 ID {id}: {ex.GetBaseException().Message}");
                        }
                    }

                    transaction.Commit();
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        transaction.RollBack();
                    }

                    throw;
                }

                var activatedFallback = view.Id != activeView.Id;
                if (activatedFallback)
                {
                    try
                    {
                        new UIDocument(document).ActiveView = view;
                    }
                    catch
                    {
                        activatedFallback = false;
                    }
                }

                return new JsonObject
                {
                    ["applied"] = formatted > 0,
                    ["formatted_count"] = formatted,
                    ["view_id"] = view.Id.Value,
                    ["view_name"] = view.Name,
                    ["activated_fallback_view"] = activatedFallback,
                    ["line_length_mm"] = lineLengthMm,
                    ["both_end_bubbles"] = formatted > 0,
                    ["failures"] = failures.Count == 0 ? null : failures
                };
            });
    }

    private async Task<JsonObject> ApplyGridPresentationAsync(
        AIToolContext context,
        JsonObject payload,
        IReadOnlyCollection<long>? gridIds,
        CancellationToken cancellationToken)
    {
        var ids = gridIds is { Count: > 0 }
            ? gridIds.ToList()
            : ReadIds(payload, "created_grids", "gridId", "grid_id", "id");

        return await ExecuteInRevitAsync(
            context,
            cancellationToken,
            document =>
            {
                var targets = ids.Count > 0
                    ? ids
                    : CollectElementIds(document, typeof(Grid));
                if (targets.Count == 0)
                {
                    return NotApplied("No grids available to format.");
                }

                var formattedViews = new JsonArray();
                var failures = new JsonArray();
                var overrideCount = 0;
                var skipped = 0;

                using var transaction = new Transaction(document, "Revit AI 设置轴网显示");
                transaction.Start();
                try
                {
                    TryColorGridCategory(document, failures);

                    foreach (var view in FindGridFormatViews(document))
                    {
                        var viewFormatted = 0;
                        foreach (var id in targets)
                        {
                            if (document.GetElement(new ElementId(id))
                                is not Grid grid)
                            {
                                continue;
                            }

                            if (!TryFormatGridInView(view, grid, out var error))
                            {
                                // A null error means the datum simply does not
                                // belong to this view, which is not a failure.
                                if (string.IsNullOrEmpty(error))
                                {
                                    skipped++;
                                }
                                else
                                {
                                    failures.Add(
                                        $"{view.Name} / {grid.Name}: {error}");
                                }

                                continue;
                            }

                            viewFormatted++;
                            overrideCount++;
                        }

                        if (viewFormatted > 0)
                        {
                            formattedViews.Add(view.Name);
                        }
                    }

                    transaction.Commit();
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        transaction.RollBack();
                    }

                    throw;
                }

                return new JsonObject
                {
                    ["applied"] = overrideCount > 0,
                    ["color"] = "RGB(255,0,0)",
                    ["override_count"] = overrideCount,
                    ["skipped_count"] = skipped,
                    ["extended_lines"] = overrideCount > 0,
                    ["formatted_views"] = formattedViews,
                    ["both_end_bubbles"] = overrideCount > 0,
                    ["failures"] = failures.Count == 0 ? null : failures
                };
            });
    }

    private static List<long> CollectElementIds(Document document, Type elementType)
    {
        try
        {
            return new FilteredElementCollector(document)
                .OfClass(elementType)
                .ToElementIds()
                .Select(id => id.Value)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<long> CollectGridIds(Document? document)
    {
        return document is null
            ? []
            : CollectElementIds(document, typeof(Grid));
    }

    private static List<ViewPlan> FindGridFormatViews(Document document)
    {
        var candidates = new FilteredElementCollector(document)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(view => !view.IsTemplate)
            .Where(view =>
                view.ViewType == ViewType.FloorPlan
                || view.ViewType == ViewType.EngineeringPlan)
            .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var views = new List<ViewPlan>();
        if (document.ActiveView is ViewPlan active
            && candidates.Any(view => view.Id == active.Id))
        {
            views.Add(active);
        }

        foreach (var view in candidates)
        {
            if (views.Count >= MaxGridViews)
            {
                break;
            }

            if (views.Any(existing => existing.Id == view.Id))
            {
                continue;
            }

            views.Add(view);
        }

        return views;
    }

    private static void TryColorGridCategory(Document document, JsonArray failures)
    {
        try
        {
            document.Settings.Categories
                .get_Item(BuiltInCategory.OST_Grids)
                .LineColor = GridRed;
        }
        catch (Exception ex)
        {
            failures.Add($"category: {ex.GetBaseException().Message}");
        }
    }

    private static bool TryFormatGridInView(
        ViewPlan view,
        Grid grid,
        out string? error)
    {
        error = null;
        try
        {
            // Datum planes that are not part of a view reject every bubble call
            // with ArgumentException ("The datum plane cannot be visible in the
            // view"), so probe first and skip the pair silently instead of
            // aborting the remaining grids of that view.
            if (!CanShowDatumInView(grid, view))
            {
                return false;
            }

            view.SetElementOverrides(
                grid.Id,
                new OverrideGraphicSettings()
                    .SetProjectionLineColor(GridRed)
                    .SetProjectionLineWeight(2));

            grid.SetDatumExtentType(
                DatumEnds.End0,
                view,
                DatumExtentType.ViewSpecific);
            grid.SetDatumExtentType(
                DatumEnds.End1,
                view,
                DatumExtentType.ViewSpecific);

            var extended = TryBuildExtendedGridCurve(view, grid);
            if (extended is not null)
            {
                grid.SetCurveInView(DatumExtentType.ViewSpecific, view, extended);
            }

            grid.ShowBubbleInView(DatumEnds.End0, view);
            grid.ShowBubbleInView(DatumEnds.End1, view);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool CanShowDatumInView(DatumPlane datum, View view)
    {
        try
        {
            _ = datum.IsBubbleVisibleInView(DatumEnds.End0, view);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Curve? TryBuildExtendedGridCurve(View view, Grid grid)
    {
        try
        {
            var curve = grid.Curve;
            if (curve is null || !curve.IsBound)
            {
                return null;
            }

            var start = curve.GetEndPoint(0);
            var end = curve.GetEndPoint(1);
            var direction = end - start;
            if (direction.GetLength() < 1e-6)
            {
                return null;
            }

            direction = direction.Normalize();
            var padding = GridPaddingMm / MillimetresPerFoot;
            var extended = Line.CreateBound(
                start - direction * padding,
                end + direction * padding);
            return grid.IsCurveValidInView(
                DatumExtentType.ViewSpecific,
                view,
                extended)
                ? extended
                : null;
        }
        catch
        {
            return null;
        }
    }
    private async Task<JsonObject> ExecuteInRevitAsync(
        AIToolContext context,
        CancellationToken cancellationToken,
        Func<Document, JsonObject> action)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var externalEvent = _adapter.GetAIToolExternalEvent()
            ?? throw new InvalidOperationException(
                "Revit AI ExternalEvent 尚未初始化。");

        var runner = new RevitExternalEventHandler(
            externalEvent,
            null,
            "Revit AI Datum Presentation");

        var result = await runner.ExecuteAsync(
            context,
            requiresTransaction: false,
            toolContext =>
            {
                if (toolContext.Document is not Document document)
                {
                    throw new InvalidOperationException(
                        "当前没有可用的 Revit 文档。");
                }

                return Task.FromResult(
                    AIToolResult.Ok("Datum presentation applied.", action(document)));
            });

        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.Error ?? "Datum presentation failed.");
        }

        return result.Data as JsonObject ?? new JsonObject
        {
            ["applied"] = false,
            ["reason"] = "显示设置没有返回结果。"
        };
    }

    private static List<long> ReadIds(
        JsonObject payload,
        string arrayName,
        params string[] idNames)
    {
        var result = new List<long>();
        var entries = payload["data"]?[arrayName]?.AsArray();
        if (entries is null)
        {
            return result;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            foreach (var idName in idNames)
            {
                if (entry[idName] is not JsonValue value)
                {
                    continue;
                }

                if (value.TryGetValue<long>(out var longValue))
                {
                    result.Add(longValue);
                    break;
                }

                if (value.TryGetValue<int>(out var intValue))
                {
                    result.Add(intValue);
                    break;
                }
            }
        }

        return result.Distinct().ToList();
    }

    private static (double Min, double Max) GetHorizontalRange(
        Document document,
        View view)
    {
        var direction = Normalize(view.RightDirection);
        var values = new List<double>();

        foreach (var grid in new FilteredElementCollector(document)
                     .OfClass(typeof(Grid))
                     .Cast<Grid>())
        {
            var curve = grid.Curve;
            if (curve is null)
            {
                continue;
            }

            values.Add(curve.GetEndPoint(0).DotProduct(direction));
            values.Add(curve.GetEndPoint(1).DotProduct(direction));
        }

        var cropBox = view.CropBox;
        if (cropBox is not null)
        {
            var transform = cropBox.Transform;
            foreach (var x in new[] { cropBox.Min.X, cropBox.Max.X })
            {
                foreach (var y in new[] { cropBox.Min.Y, cropBox.Max.Y })
                {
                    foreach (var z in new[] { cropBox.Min.Z, cropBox.Max.Z })
                    {
                        values.Add(
                            transform.OfPoint(new XYZ(x, y, z))
                                .DotProduct(direction));
                    }
                }
            }
        }

        if (values.Count == 0)
        {
            var half = MinimumDatumLengthMm / MillimetresPerFoot / 2.0;
            return (-half, half);
        }

        var min = values.Min() - DatumPaddingMm / MillimetresPerFoot;
        var max = values.Max() + DatumPaddingMm / MillimetresPerFoot;
        var minimumLength = MinimumDatumLengthMm / MillimetresPerFoot;
        if (max - min < minimumLength)
        {
            var center = (min + max) / 2.0;
            min = center - minimumLength / 2.0;
            max = center + minimumLength / 2.0;
        }

        return (min, max);
    }

    private static Line CreateHorizontalLine(
        View view,
        double elevation,
        (double Min, double Max) range,
        out double lengthMm)
    {
        var direction = Normalize(view.RightDirection);
        var start = new XYZ(0, 0, elevation) + direction * range.Min;
        var end = new XYZ(0, 0, elevation) + direction * range.Max;
        lengthMm = start.DistanceTo(end) * MillimetresPerFoot;
        return Line.CreateBound(start, end);
    }

    private static XYZ Normalize(XYZ vector)
    {
        if (vector.GetLength() < 1e-9)
        {
            return XYZ.BasisX;
        }

        return vector.Normalize();
    }

    private static JsonObject NotApplied(string reason)
    {
        return new JsonObject
        {
            ["applied"] = false,
            ["reason"] = reason
        };
    }

    private static ViewSection? FindElevationView(Document document)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(ViewSection))
            .Cast<ViewSection>()
            .Where(view => !view.IsTemplate
                           && view.ViewType == ViewType.Elevation)
            .OrderByDescending(view =>
                view.Name.Contains("南", StringComparison.OrdinalIgnoreCase)
                || view.Name.Contains("H", StringComparison.OrdinalIgnoreCase))
            .ThenBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

