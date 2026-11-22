using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // Experimental inspection only.
    // Never writes to game, Penumbra or Glamourer state.

    private sealed class UvReport
    {
        public string Texture = "";
        public string Status = "";
        public int Meshes;
        public int Triangles;
        public int SkippedTriangles;
        public int Samples;
        public int CleanSamples;
        public int OffGridSamples;
        public bool Capped;

        public readonly double[] Weights = new double[32];

        // Unique occupied cells for this material.
        // Never count an overlapping UV cell twice.
        public readonly byte[] Mask =
            new byte[UvMaskSize * UvMaskSize];


    }

    private sealed class TexturePixels
    {
        public required byte[] Bgra;
        public required int Width;
        public required int Height;
    }

    private readonly Dictionary<MaterialEntry, UvReport>
        uvReports = new();

    private readonly Dictionary<ModelEntry, string>
        uvModelStatus = new();

    private bool flipUvV;
    private float minimumUvPercent = 0.5f;

    private int uvMatchedModels;
    private int uvMatchedMeshes;
    private int uvUnmatchedMeshes;
    private int uvTriangles;



    private static string BaseName(string path)
    {
        return Path.GetFileName(path.Replace('\\', '/'));
    }

    private MdlFile? LoadModelFile(ModelEntry model)
    {
        string actual = model.ActualPath;
        string game = model.GamePath;

        if (Path.IsPathRooted(actual))
        {
            var info = new FileInfo(actual);

            if (!info.Exists ||
                info.Length > 64L * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "Resolved model absent or over 64 MiB.");
            }

            return Data.GameData.GetFileFromDisk<AdriGeometryMdlFile>(
                actual, game);
        }

        return Data.GetFile<AdriGeometryMdlFile>(
            string.IsNullOrWhiteSpace(actual)
                ? game
                : actual);
    }

    private TexturePixels LoadIdPixels(
        TextureEntry texture,
        Dictionary<string, TexturePixels> cache,
        ref long bytes)
    {
        string actual = texture.ActualPath;

        string key = string.IsNullOrWhiteSpace(actual)
            ? texture.GamePath
            : actual;

        if (cache.TryGetValue(key, out var existing))
            return existing;

        TexFile? file;

        if (Path.IsPathRooted(actual))
        {
            var info = new FileInfo(actual);

            if (!info.Exists ||
                info.Length > 64L * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "Resolved ID texture absent or over 64 MiB.");
            }

            file = Data.GameData.GetFileFromDisk<TexFile>(
                actual);
        }
        else
        {
            file = Data.GetFile<TexFile>(key);
        }

        if (file == null)
        {
            throw new InvalidOperationException(
                "ID texture unavailable.");
        }

        int width = file.Header.Width;
        int height = file.Header.Height;

        if (width <= 0 ||
            height <= 0 ||
            (long)width * height > 8_388_608)
        {
            throw new InvalidOperationException(
                "Unsupported texture dimensions.");
        }

        byte[] bgra = file.ImageData;

        if (bgra.Length < (long)width * height * 4)
        {
            throw new InvalidOperationException(
                "Incomplete decoded ID texture.");
        }

        if (bytes + bgra.LongLength >
            96L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "96 MiB temporary texture budget reached.");
        }

        bytes += bgra.LongLength;

        var result = new TexturePixels
        {
            Bgra = bgra,
            Width = width,
            Height = height
        };

        cache.Add(key, result);

        return result;
    }

    // ======================================================
    // MESH ANALYSIS
    // ======================================================

    // Direct UV reader avoids Lumina.Models.Mesh.GetVector4.
    // We still rely on Lumina's MdlFile container parsing.

    private sealed class RawUvMesh
    {
        public string MaterialName = "";
        public Vector2?[] Uvs = Array.Empty<Vector2?>();
        public ushort[] Indices = Array.Empty<ushort>();

    }

    private readonly Queue<ModelEntry> pendingUvModels = new();
    private readonly HashSet<MaterialEntry> uvIncompleteMaterials = new();

    private readonly Dictionary<string, TexturePixels> uvPixelCache =
        new(StringComparer.OrdinalIgnoreCase);

    private long uvCacheBytes;
    private int uvTotalModels;
    private int uvProcessedModels;

    private sealed class MeshIssue
    {
        public ModelEntry Model = null!;
        public int MeshIndex;
        public string Category = "";
        public string Material = "";
        public string Detail = "";
    }

    private readonly List<MeshIssue> meshIssues = new();

    private void RecordMeshIssue(
        ModelEntry model,
        int meshIndex,
        string category,
        string material,
        string detail)
    {
        meshIssues.Add(new MeshIssue
        {
            Model = model,
            MeshIndex = meshIndex,
            Category = category,
            Material = material,
            Detail = detail
        });
    }

    private void DrawMeshIssueDiagnostics()
    {
        if (pendingUvModels.Count > 0)
        {
            ImGui.TextDisabled(
                "Classification available when scan completes.");
            return;
        }

        if (meshIssues.Count == 0)
        {
            ImGui.TextDisabled("No exclusions recorded.");
            return;
        }

        if (!ImGui.TreeNode(
            $"Excluded mesh details ({meshIssues.Count})##ADRIExclusions"))
            return;

        ImGui.TextDisabled(
            "Reasons use actual parser and resource-link results.");
        ImGui.TextDisabled(
            "Non-outfit does not necessarily mean invisible.");

        foreach (var group in meshIssues
            .GroupBy(x => x.Category)
            .OrderByDescending(x => x.Count()))
        {
            ImGui.TextUnformatted(
                $"{group.Key} ({group.Count()})");

            ImGui.Indent();

            foreach (var issue in group)
            {
                string label = issue.MeshIndex < 0
                    ? "Model"
                    : $"Mesh {issue.MeshIndex}";

                ImGui.BulletText(
                    $"{issue.Model.Slot} / {label} / " +
                    issue.Material);

                ImGui.TextWrapped(issue.Detail);
            }

            ImGui.Unindent();
            ImGui.Spacing();
        }

        ImGui.TreePop();
    }

    private void AppendMeshIssueDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("[MESH CLASSIFICATION]");

        sb.AppendLine(
            $"Recorded issues: {meshIssues.Count}; " +
            $"unmatched mesh counter: {uvUnmatchedMeshes}");

        foreach (var issue in meshIssues)
        {
            sb.AppendLine(
                $"[{issue.Category}] " +
                $"slot={issue.Model.Slot} " +
                $"mesh={issue.MeshIndex} " +
                $"material={issue.Material}");

            sb.AppendLine("  " + issue.Detail);
            sb.AppendLine("  Model: " + issue.Model.GamePath);
        }

        sb.AppendLine();
    }
    private void AnalyzeGeometry()
    {
        // This is called after the Penumbra resource scan.
        // Queue work rather than parsing all models inside
        // the Scan button's ImGui callback.

        uvReports.Clear();
        uvModelStatus.Clear();
        meshIssues.Clear();
        pendingUvModels.Clear();
        uvPixelCache.Clear();
        uvIncompleteMaterials.Clear();
        ResetStructureAudit();

        uvCacheBytes = 0;
        uvMatchedModels = 0;
        uvMatchedMeshes = 0;
        uvUnmatchedMeshes = 0;
        uvTriangles = 0;
        uvProcessedModels = 0;
        ResetRasterCounters();
        ResetUvFairness();

        foreach (var model in models.Where(x => x.IsOutfit))
            pendingUvModels.Enqueue(model);

        uvTotalModels = pendingUvModels.Count;
    }

    private void ProcessGeometryStep()
    {
        if (pendingUvModels.Count == 0)
            return;

        // Exactly one model is processed per UI frame.
        var model = pendingUvModels.Dequeue();
        BeginUvModelBudget();

        try
        {
            var mdl = LoadModelFile(model);

            if (mdl == null)
            {
                uvModelStatus[model] = "MDL unavailable";
                return;
            }

            if (mdl.Data == null ||
                mdl.Data.Length > 64L * 1024 * 1024)
            {
                uvModelStatus[model] =
                    "MDL unavailable or exceeds 64 MiB";
                return;
            }

            if (mdl.Lods == null ||
                mdl.Lods.Length == 0 ||
                mdl.Meshes == null)
            {
                uvModelStatus[model] =
                    "No usable model LOD or mesh records";
                return;
            }

            var lod = mdl.Lods[0];
            int first = lod.MeshIndex;
            int end = first + lod.MeshCount;

            if (first < 0 ||
                end > mdl.Meshes.Length ||
                end < first)
            {
                uvModelStatus[model] =
                    "Invalid high-LOD mesh range";
                return;
            }

            AuditGeometryModel(model, mdl, first, end);

            int linked = 0;
            int skipped = 0;
            var failures = new List<string>();

            for (int meshIndex = first;
                 meshIndex < end;
                 meshIndex++)
            {
                var meshRecord = mdl.Meshes[meshIndex];

                // Zero-size mesh records are legitimate
                // structural placeholders, not read failures.
                if (meshRecord.VertexCount == 0 &&
                    meshRecord.IndexCount == 0)
                {
                    uvEmptyMeshes++;
                    skipped++;

                    RecordMeshIssue(
                        model, meshIndex,
                        "Empty structural mesh",
                        "(no geometry)",
                        "Zero vertices and indices. " +
                        "No UV sampling required.");
                    continue;
                }

                string materialName;

                try
                {
                    materialName = ReadMaterialName(
                        mdl, meshRecord.MaterialIndex);
                }
                catch (Exception ex)
                {
                    skipped++;
                    uvUnmatchedMeshes++;

                    RecordMeshIssue(
                        model, meshIndex,
                        "Invalid material reference",
                        "(unresolved)",
                        ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                // Prefer exact material names. Only when
                // none exist do we consider a unique race-
                // prefix-normalized Penumbra tree link.
                var exact = model.Materials
                    .Where(m => string.Equals(
                        BaseName(m.GamePath),
                        materialName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                bool raceVariant = exact.Count == 0;

                var candidates = raceVariant
                    ? model.Materials
                        .Where(m => IsRaceVariantName(
                            materialName,
                            BaseName(m.GamePath)))
                        .ToList()
                    : exact;

                if (candidates.Count != 1)
                {
                    foreach (var candidate in candidates
                        .Where(IsOutfitMaterial))
                    {
                        uvIncompleteMaterials.Add(candidate);
                    }

                    skipped++;
                    uvUnmatchedMeshes++;

                    string category = candidates.Count == 0
                        ? "Unresolved material binding"
                        : "Ambiguous material binding";

                    RecordMeshIssue(
                        model, meshIndex,
                        category,
                        materialName,
                        $"{candidates.Count} Penumbra-linked " +
                        "candidates after exact-first matching. " +
                        "No unverified candidate was selected.");

                    if (failures.Count < 5)
                        failures.Add($"Mesh {meshIndex}: {category}");

                    continue;
                }

                var material = candidates[0];

                // Record but do not rasterize skin and other
                // non-equipment resources under the current
                // outfit-only analysis policy.
                if (!IsOutfitMaterial(material))
                {
                    uvKnownNonOutfitMeshes++;
                    skipped++;

                    if (raceVariant)
                        uvRaceVariantLinks++;

                    RecordMeshIssue(
                        model, meshIndex,
                        raceVariant
                            ? "Linked race-variant non-outfit resource"
                            : "Linked non-outfit resource",
                        materialName,
                        "Unique Penumbra model-tree candidate: " +
                        material.GamePath +
                        (raceVariant
                            ? " (matches after c#### normalization). "
                            : " (exact basename). ") +
                        "Excluded from outfit-only UV sampling; " +
                        "not proof of invisibility or shader binding.");

                    continue;
                }

                if (raceVariant)
                {
                    uvRaceVariantLinks++;
                    uvRaceVariantMaterials.Add(material);

                    RecordMeshIssue(
                        model, meshIndex,
                        "Provisional race-variant outfit binding",
                        materialName,
                        "Unique Penumbra-linked candidate: " +
                        material.GamePath +
                        ". Not exact-name or shader verification.");
                }

                // Check budgets only after identifying
                // the material and excluding empty/non-
                // outfit geometry. Later models retain
                // their independently reserved allowance.
                if (!HasUvBudget())
                {
                    FlagUvBudgetStop();
                    uvIncompleteMaterials.Add(material);
                    skipped++;
                    uvUnmatchedMeshes++;

                    string reason = DescribeUvBudgetStop();

                    RecordMeshIssue(
                        model, meshIndex,
                        "UV safety-budget omission",
                        materialName,
                        reason + " No UV data sampled for mesh.");

                    if (failures.Count < 5)
                        failures.Add($"Mesh {meshIndex}: {reason}");

                    continue;
                }

                RawUvMesh raw;

                try
                {
                    raw = ReadUvMesh(mdl, meshIndex);
                }
                catch (Exception ex)
                {
                    uvIncompleteMaterials.Add(material);
                    skipped++;
                    uvUnmatchedMeshes++;

                    RecordMeshIssue(
                        model, meshIndex,
                        "Geometry read failure",
                        materialName,
                        ex.GetType().Name + ": " + ex.Message);

                    if (failures.Count < 5)
                        failures.Add($"Mesh {meshIndex}: {ex.Message}");

                    continue;
                }

                var ids = material.Textures
                    .Where(t => t.IsId &&
                        t.Decoded?.Success == true)
                    .GroupBy(
                        t => string.IsNullOrWhiteSpace(t.ActualPath)
                            ? t.GamePath
                            : t.ActualPath,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (ids.Count != 1)
                {
                    uvIncompleteMaterials.Add(material);
                    skipped++;
                    uvUnmatchedMeshes++;

                    string reason = ids.Count == 0
                        ? "No unique decoded ID texture"
                        : "Ambiguous ID texture sources";

                    RecordMeshIssue(
                        model, meshIndex,
                        reason,
                        materialName,
                        $"{ids.Count} resolved decoded ID " +
                        "texture candidates. Shader usage unverified.");

                    if (failures.Count < 5)
                        failures.Add($"Mesh {meshIndex}: {reason}");

                    continue;
                }

                if (!uvReports.TryGetValue(
                    material, out var report))
                {
                    report = new UvReport
                    {
                        Texture = ids[0].GamePath
                    };

                    uvReports.Add(material, report);
                }

                try
                {
                    var pixels = LoadIdPixels(
                        ids[0],
                        uvPixelCache,
                        ref uvCacheBytes);

                    bool complete =
                        RasterizeMesh(raw, pixels, report);

                    if (complete)
                    {
                        linked++;
                        report.Meshes++;
                        uvMatchedMeshes++;
                    }
                    else
                    {
                        uvIncompleteMaterials.Add(material);
                        skipped++;
                        uvUnmatchedMeshes++;

                        RecordMeshIssue(
                            model, meshIndex,
                            "Partial UV rasterization",
                            materialName,
                            report.Status.Length > 0
                                ? report.Status
                                : "Incomplete UV triangle coverage.");

                        if (failures.Count < 5)
                            failures.Add(
                                $"Mesh {meshIndex}: partial UV coverage");
                    }
                }
                catch (Exception ex)
                {
                    uvIncompleteMaterials.Add(material);
                    report.Capped = true;
                    report.Status = ex.Message;

                    skipped++;
                    uvUnmatchedMeshes++;

                    RecordMeshIssue(
                        model, meshIndex,
                        "Texture or UV sampling failure",
                        materialName,
                        ex.GetType().Name + ": " + ex.Message);

                    if (failures.Count < 5)
                        failures.Add($"Mesh {meshIndex}: {ex.Message}");
                }
            }

            if (linked > 0)
                uvMatchedModels++;

            uvModelStatus[model] =
                $"{linked} fully sampled mesh(es), " +
                $"{skipped} excluded/incomplete. " +
                string.Join(" | ", failures);
        }
        catch (Exception ex)
        {
            RecordMeshIssue(
                model, -1,
                "Model processing failure",
                "(model)",
                ex.GetType().Name + ": " + ex.Message);

            uvModelStatus[model] =
                "Model processing failed: " +
                ex.GetType().Name + ": " + ex.Message;

            Log.Warning(
                $"ADRI Stage 2D model processing failed: {ex}");
        }
        finally
        {
            uvProcessedModels++;

            if (pendingUvModels.Count == 0)
            {
                uvPixelCache.Clear();
                uvCacheBytes = 0;
            }
        }
    }

    private static string ReadMaterialName(
        MdlFile mdl, int materialIndex)
    {
        if (materialIndex < 0 ||
            materialIndex >= mdl.MaterialNameOffsets.Length)
        {
            throw new InvalidDataException(
                "Material index outside model table");
        }

        int offset = checked(
            (int)mdl.MaterialNameOffsets[materialIndex]);

        byte[] strings = mdl.Strings;

        if (offset < 0 || offset >= strings.Length)
        {
            throw new InvalidDataException(
                "Material name offset invalid");
        }

        int end = Array.IndexOf(
            strings, (byte)0, offset);

        if (end < 0)
        {
            throw new InvalidDataException(
                "Material name is not terminated");
        }

        string path = Encoding.UTF8.GetString(
            strings, offset, end - offset);

        return BaseName(path);
    }

    private static RawUvMesh ReadUvMesh(
        MdlFile mdl,
        int meshIndex)
    {
        if (meshIndex < 0 ||
            meshIndex >= mdl.Meshes.Length ||
            meshIndex >= mdl.VertexDeclarations.Length)
        {
            throw new InvalidDataException(
                "Mesh/declaration index invalid");
        }

        var mesh = mdl.Meshes[meshIndex];
        var declaration = mdl.VertexDeclarations[meshIndex];

        if (mesh.VertexCount == 0 ||
            mesh.IndexCount < 3 ||
            mesh.IndexCount > 240000)
        {
            throw new InvalidDataException(
                "Mesh has unsupported vertex/index count");
        }

        // Read UV usage index zero only.
        // Other attributes are irrelevant to ADRI.
        var uvElements = declaration.VertexElements
            .Where(e => e.Usage == 4 && e.UsageIndex == 0)
            .ToArray();

        if (uvElements.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one UV0 element, found {uvElements.Length}");
        }

        var uv = uvElements[0];

        int byteSize = uv.Type switch
        {
            1 => 8,   // Float2
            2 => 12,  // Float3
            3 => 16,  // Float4
            8 => 4,   // Normalized byte4
            13 => 4,  // Half2
            14 => 8,  // Half4
            _ => 0
        };

        if (byteSize == 0)
        {
            throw new InvalidDataException(
                $"Unsupported UV0 vertex type {uv.Type}");
        }

        int stream = uv.Stream;

        if (stream < 0 ||
            stream >= 3 ||
            stream >= mesh.VertexStreamCount)
        {
            throw new InvalidDataException(
                "UV vertex stream index invalid");
        }

        int stride = mesh.VertexBufferStride[stream];

        if (stride <= 0 ||
            uv.Offset + byteSize > stride)
        {
            throw new InvalidDataException(
                "Invalid UV stride or offset");
        }

        byte[] data = mdl.Data;

        long vertexStart =
            mdl.FileHeader.VertexOffset[0];

        long vertexEnd = vertexStart +
            mdl.FileHeader.VertexBufferSize[0];

        long streamStart = vertexStart +
            mesh.VertexBufferOffset[stream];

        if (vertexStart < 0 ||
            vertexEnd > data.LongLength ||
            streamStart < vertexStart ||
            streamStart >= vertexEnd)
        {
            throw new InvalidDataException(
                "Vertex buffer range invalid");
        }

        var result = new RawUvMesh
        {
            MaterialName = ReadMaterialName(
                mdl, mesh.MaterialIndex),
            Uvs = new Vector2?[mesh.VertexCount],
            Indices = new ushort[checked((int)mesh.IndexCount)]
        };

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            long position = streamStart +
                (long)i * stride + uv.Offset;

            if (position < vertexStart ||
                position + byteSize > vertexEnd ||
                position + byteSize > data.LongLength)
            {
                throw new InvalidDataException(
                    "UV vertex data outside buffer");
            }

            int pos = checked((int)position);

            float u;
            float v;

            switch (uv.Type)
            {
                case 1:
                case 2:
                case 3:
                    u = BitConverter.ToSingle(data, pos);
                    v = BitConverter.ToSingle(data, pos + 4);
                    break;

                case 13:
                case 14:
                    u = (float)BitConverter.UInt16BitsToHalf(
                        BitConverter.ToUInt16(data, pos));

                    v = (float)BitConverter.UInt16BitsToHalf(
                        BitConverter.ToUInt16(data, pos + 2));
                    break;

                case 8:
                    u = data[pos] / 255f;
                    v = data[pos + 1] / 255f;
                    break;

                default:
                    throw new InvalidDataException(
                        "Unsupported UV data type");
            }

            if (float.IsFinite(u) &&
                float.IsFinite(v))
            {
                result.Uvs[i] = new Vector2(u, v);
            }
        }

        long indexStart =
            mdl.FileHeader.IndexOffset[0] +
            (long)mesh.StartIndex * 2;

        long indexEnd =
            mdl.FileHeader.IndexOffset[0] +
            mdl.FileHeader.IndexBufferSize[0];

        long requiredEnd =
            indexStart + result.Indices.LongLength * 2;

        if (indexStart < mdl.FileHeader.IndexOffset[0] ||
            requiredEnd > indexEnd ||
            requiredEnd > data.LongLength)
        {
            throw new InvalidDataException(
                "Index data outside buffer");
        }

        for (int i = 0; i < result.Indices.Length; i++)
        {
            result.Indices[i] = BitConverter.ToUInt16(
                data, checked((int)(indexStart + (long)i * 2)));
        }

        return result;
    }



    private IEnumerable<(string Row, double Percent)>
        RankedUvRows(UvReport report)
    {
        if (report.CleanSamples == 0)
        {
            return Enumerable.Empty<(string, double)>();
        }

        return report.Weights
            .Select((w, i) => (
                Row: $"{i / 2 + 1}{(i % 2 == 0 ? "A" : "B")}",
                Percent: 100.0 * w / report.CleanSamples
            ))
            .Where(x => x.Percent >= minimumUvPercent)
            .OrderByDescending(x => x.Percent);
    }

    // ======================================================
    // UI
    // ======================================================

    private void DrawUvModelStatus(ModelEntry model)
    {
        if (uvModelStatus.TryGetValue(
            model, out var message))
        {
            ImGui.TextDisabled(
                "Experimental mesh parse: " + message);
        }
    }

    private void DrawUvReport(MaterialEntry material)
    {
        if (!IsOutfitMaterial(material))
            return;

        if (!uvReports.TryGetValue(
            material, out var report))
        {
            ImGui.TextDisabled(
                uvIncompleteMaterials.Contains(material)
                    ? "UV: incomplete; one or more linked meshes " +
                      "could not be sampled."
                    : "UV: not sampled " +
                      "(mesh/material could not be uniquely matched).");
            return;
        }

        ImGui.TextUnformatted(
            $"UV0 rasterized: {report.Triangles} triangles");

        ImGui.TextDisabled(
            $"256x256 mask: {report.Samples:N0} unique " +
            $"covered cells / {UvMaskSize * UvMaskSize:N0}");

        ImGui.TextDisabled(
            $"Raster checks: {uvRasterChecks:N0} / " +
            $"{MaxUvRasterChecks:N0} global budget");

        ImGui.TextDisabled(
            $"ID: {BaseName(report.Texture)}; " +
            $"valid-palette {report.CleanSamples}, " +
            $"off-grid {report.OffGridSamples}, " +
            $"skipped triangles {report.SkippedTriangles}");

        if (report.Capped ||
            uvIncompleteMaterials.Contains(material))
        {
            ImGui.TextDisabled(
                "PARTIAL: UV coverage is incomplete. " +
                "See mesh and budget diagnostics.");
        }

        if (!string.IsNullOrWhiteSpace(report.Status))
        {
            ImGui.TextDisabled(
                "UV warning: " + report.Status);
        }

        if (uvRaceVariantMaterials.Contains(material))
        {
            ImGui.TextDisabled(
                "PROVISIONAL: race-variant material-name " +
                "match. Runtime shader use is unverified.");
        }

        var rows = RankedUvRows(report).ToArray();

        if (rows.Length == 0)
        {
            ImGui.TextDisabled(
                "No sampled rows pass the UV threshold.");
            return;
        }

        ImGui.TextWrapped(
            "UV-sampled rows: " +
            string.Join(", ", rows.Select(x => x.Row)));

        foreach (var row in rows.Take(12))
        {
            ImGui.TextWrapped(
                $"  {row.Row}: {row.Percent:F2}% sample influence");
        }

        if (rows.Length > 12)
        {
            ImGui.TextDisabled(
                $"{rows.Length - 12} additional UV rows; " +
                "see diagnostics.");
        }

        if (ImGui.SmallButton(
            $"Copy UV Rows##uv_{material.GetHashCode()}"))
        {
            ImGui.SetClipboardText(
                string.Join(", ", rows.Select(x => x.Row)));
        }

        ImGui.TextDisabled(
            "Experimental UV0 coverage mask. ID texture " +
            "selection is not yet shader-verified.");

        ImGui.TextDisabled(
            "Active shapes, attributes and rendered " +
            "visibility are not yet verified.");
    }

    private void DrawUvControls()
    {
        ImGui.TextDisabled(
            $"Geometry scan: {uvProcessedModels}/{uvTotalModels} models processed; " +
            $"{pendingUvModels.Count} pending.");
        ImGui.TextDisabled(
            "DEV8: experimental 256x256 UV coverage " +
            "(rescan after UV changes).");

        ImGui.Checkbox(
            "Flip UV V (rescan required)", ref flipUvV);

        ImGui.SliderFloat(
            "Minimum UV sample influence (%)",
            ref minimumUvPercent,
            0.0f,
            10.0f,
            "%.2f%%");

        ImGui.TextDisabled(
            $"UV: {uvMatchedModels} model(s), " +
            $"{uvMatchedMeshes} complete meshes, " +
            $"{uvUnmatchedMeshes} incomplete/unresolved.");
        ImGui.TextDisabled(
            $"Non-outfit: {uvKnownNonOutfitMeshes}, " +
            $"empty: {uvEmptyMeshes}, " +
            $"race-variant links: {uvRaceVariantLinks}.");
        ImGui.TextDisabled(
            $"Triangles: {uvTriangles:N0} rasterized, " +
            $"{uvTriangleAttempts:N0} attempted; " +
            $"per-model allowance {uvModelTriangleBudget:N0}.");
    }

    // ======================================================
    // DIAGNOSTICS
    // ======================================================

    private void AppendUvDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("[EXPERIMENTAL UV COVERAGE MASK REPORT]");

        sb.AppendLine(
            "Mode: UV0/XY, repeating UV, " +
            "256x256 triangle coverage mask, unique " +
            "occupied cells per material; " +
            "NOT active-shape or rendered-visibility proof");

        sb.AppendLine("Flip V: " + flipUvV);
        sb.AppendLine(
            $"Triangle attempts: {uvTriangleAttempts}/" +
            $"{MaxUvTotalTriangleAttempts}; " +
            $"per-model cap: {uvModelTriangleBudget}");
        sb.AppendLine(
            $"Per-model raster cap: {uvModelRasterBudget}; " +
            $"model budget stops: {uvModelBudgetStops}");
        sb.AppendLine(
            $"Non-outfit meshes: {uvKnownNonOutfitMeshes}; " +
            $"empty meshes: {uvEmptyMeshes}; " +
            $"race-variant links: {uvRaceVariantLinks}");
        sb.AppendLine(
            $"Raster checks: {uvRasterChecks}/{MaxUvRasterChecks}");
        sb.AppendLine(
            "Raster work budget exceeded: " +
            uvRasterBudgetExceeded);
        sb.AppendLine(
            "Triangle limit reached: " +
            uvTriangleLimitReached);
        AppendMeshIssueDiagnostics(sb);
        AppendStructureAudit(sb);

        sb.AppendLine(
            $"Models: {uvMatchedModels}, " +
            $"meshes: {uvMatchedMeshes}, " +
            $"unmatched: {uvUnmatchedMeshes}, " +
            $"triangles: {uvTriangles}");

        foreach (var entry in uvModelStatus)
        {
            sb.AppendLine(
                $"MODEL {entry.Key.GamePath}: {entry.Value}");
        }

        foreach (var entry in uvReports)
        {
            var r = entry.Value;
            string binding = uvRaceVariantMaterials.Contains(entry.Key)
                ? "race-variant-provisional"
                : "exact-material-basename";

            sb.AppendLine(
                $"MATERIAL {entry.Key.GamePath} " +
                $"ID {r.Texture}: " +
                $"meshes={r.Meshes} " +
                $"triangles={r.Triangles} " +
                $"coveredCells={r.Samples} " +
                $"clean={r.CleanSamples} " +
                $"offgrid={r.OffGridSamples} " +
                $"skipped={r.SkippedTriangles} " +
                $"capped={r.Capped} " +
                $"incomplete={r.Capped || uvIncompleteMaterials.Contains(entry.Key)} " +
                $"binding={binding} " +
                $"status={r.Status}");

            if (r.CleanSamples > 0)
            {
                var details = r.Weights
                    .Select((w, i) => new
                    {
                        Name = $"{i / 2 + 1}" +
                               (i % 2 == 0 ? "A" : "B"),
                        Percent = 100.0 * w / r.CleanSamples
                    })
                    .Where(x => x.Percent > 0)
                    .OrderByDescending(x => x.Percent);

                foreach (var row in details)
                {
                    sb.AppendLine(
                        $"  {row.Name}: {row.Percent:F5}%");
                }
            }
        }

        sb.AppendLine();
    }
}