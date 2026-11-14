using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;
using System.Text;

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

    private static readonly Vector3[] BarycentricSamples =
    {
        new(1f/3f, 1f/3f, 1f/3f),
        new(0.8f, 0.1f, 0.1f),
        new(0.1f, 0.8f, 0.1f),
        new(0.1f, 0.1f, 0.8f),
        new(0.45f, 0.45f, 0.1f),
        new(0.1f, 0.45f, 0.45f),
        new(0.45f, 0.1f, 0.45f),
    };

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

    private readonly Dictionary<string, TexturePixels> uvPixelCache =
        new(StringComparer.OrdinalIgnoreCase);

    private long uvCacheBytes;
    private int uvTotalModels;
    private int uvProcessedModels;

    private void AnalyzeGeometry()
    {
        // This is called after the Penumbra resource scan.
        // Queue work rather than parsing all models inside
        // the Scan button's ImGui callback.

        uvReports.Clear();
        uvModelStatus.Clear();
        pendingUvModels.Clear();
        uvPixelCache.Clear();

        uvCacheBytes = 0;
        uvMatchedModels = 0;
        uvMatchedMeshes = 0;
        uvUnmatchedMeshes = 0;
        uvTriangles = 0;
        uvProcessedModels = 0;

        foreach (var model in models.Where(x => x.IsOutfit))
            pendingUvModels.Enqueue(model);

        uvTotalModels = pendingUvModels.Count;
    }

    private void ProcessGeometryStep()
    {
        if (pendingUvModels.Count == 0)
            return;

        // Exactly one model per UI frame.
        var model = pendingUvModels.Dequeue();

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

            int linked = 0;
            int skipped = 0;

            var failures = new List<string>();

            for (int meshIndex = first;
                 meshIndex < end;
                 meshIndex++)
            {
                if (uvTriangles >= 60000)
                {
                    failures.Add(
                        "Global 60,000 triangle limit reached");
                    break;
                }

                RawUvMesh raw;

                try
                {
                    raw = ReadUvMesh(mdl, meshIndex);
                }
                catch (Exception ex)
                {
                    skipped++;
                    uvUnmatchedMeshes++;

                    if (failures.Count < 5)
                    {
                        failures.Add(
                            $"Mesh {meshIndex}: {ex.Message}");
                    }

                    continue;
                }

                var matches = model.Materials
                    .Where(IsOutfitMaterial)
                    .Where(m => string.Equals(
                        BaseName(m.GamePath),
                        raw.MaterialName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count != 1)
                {
                    skipped++;
                    uvUnmatchedMeshes++;

                    if (failures.Count < 5)
                    {
                        failures.Add(
                            $"Mesh {meshIndex}: material " +
                            $"'{raw.MaterialName}' has " +
                            $"{matches.Count} matches");
                    }

                    continue;
                }

                var material = matches[0];

                var ids = material.Textures
                    .Where(t => t.IsId &&
                        t.Decoded?.Success == true)
                    .GroupBy(
                        t => string.IsNullOrWhiteSpace(
                            t.ActualPath)
                            ? t.GamePath
                            : t.ActualPath,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (ids.Count != 1)
                {
                    skipped++;
                    uvUnmatchedMeshes++;

                    if (failures.Count < 5)
                    {
                        failures.Add(
                            $"Mesh {meshIndex}: " +
                            $"{ids.Count} matching ID textures");
                    }

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

                    SampleMesh(raw, pixels, report);

                    linked++;
                    report.Meshes++;
                    uvMatchedMeshes++;
                }
                catch (Exception ex)
                {
                    report.Status = ex.Message;
                    skipped++;
                    uvUnmatchedMeshes++;

                    if (failures.Count < 5)
                    {
                        failures.Add(
                            $"Mesh {meshIndex}: {ex.Message}");
                    }
                }
            }

            if (linked > 0)
                uvMatchedModels++;

            uvModelStatus[model] =
                $"{linked} matched mesh(es), " +
                $"{skipped} skipped. " +
                string.Join(" | ", failures);
        }
        catch (Exception ex)
        {
            uvModelStatus[model] =
                "Model processing failed: " +
                ex.GetType().Name + ": " + ex.Message;

            Log.Warning(
                $"ADRI DEV3 FIX1 model processing failed: {ex}");
        }
        finally
        {
            uvProcessedModels++;

            // Discard temporary decoded texture buffers
            // once the final model is processed.
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

    private void SampleMesh(
        RawUvMesh mesh,
        TexturePixels tex,
        UvReport report)
    {
        var indices = mesh.Indices;
        var vertices = mesh.Uvs;

        for (int n = 0; n + 2 < indices.Length; n += 3)
        {
            if (uvTriangles >= 60000)
            {
                report.Capped = true;
                return;
            }

            int i0 = indices[n];
            int i1 = indices[n + 1];
            int i2 = indices[n + 2];

            if (i0 >= vertices.Length ||
                i1 >= vertices.Length ||
                i2 >= vertices.Length ||
                !vertices[i0].HasValue ||
                !vertices[i1].HasValue ||
                !vertices[i2].HasValue)
            {
                report.SkippedTriangles++;
                continue;
            }

            Vector2 a = vertices[i0]!.Value;
            Vector2 b = vertices[i1]!.Value;
            Vector2 c = vertices[i2]!.Value;

            foreach (var w in BarycentricSamples)
            {
                float u =
                    a.X * w.X +
                    b.X * w.Y +
                    c.X * w.Z;

                float v =
                    a.Y * w.X +
                    b.Y * w.Y +
                    c.Y * w.Z;

                SampleUv(u, v, tex, report);
            }

            report.Triangles++;
            uvTriangles++;
        }
    }

    private void SampleUv(
        float u,
        float v,
        TexturePixels tex,
        UvReport report)
    {
        // Experimental UV0 XY sampling.
        // Uses repeating address mode and point sampling.
        // The actual shader mapping remains to be validated.

        double x = u - Math.Floor(u);
        double y = v - Math.Floor(v);

        if (flipUvV)
            y = 1.0 - y;

        int px = Math.Clamp(
            (int)(x * tex.Width),
            0,
            tex.Width - 1);

        int py = Math.Clamp(
            (int)(y * tex.Height),
            0,
            tex.Height - 1);

        int offset = (py * tex.Width + px) * 4;

        byte red = tex.Bgra[offset + 2];
        byte green = tex.Bgra[offset + 1];

        report.Samples++;

        int pair = Math.Clamp((red + 8) / 17, 0, 15);

        if (Math.Abs(red - pair * 17) > 3)
        {
            report.OffGridSamples++;
            return;
        }

        report.CleanSamples++;

        report.Weights[pair * 2] += green / 255.0;
        report.Weights[pair * 2 + 1] +=
            (255 - green) / 255.0;
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
                "UV: not sampled " +
                "(mesh/material could not be uniquely matched).");
            return;
        }

        ImGui.TextUnformatted(
            $"UV0 sampled: {report.Triangles} triangles, " +
            $"{report.Samples} samples");

        ImGui.TextDisabled(
            $"ID: {BaseName(report.Texture)}; " +
            $"valid-palette {report.CleanSamples}, " +
            $"off-grid {report.OffGridSamples}, " +
            $"skipped triangles {report.SkippedTriangles}");

        if (report.Capped)
        {
            ImGui.TextDisabled(
                "PARTIAL: global triangle safety limit reached.");
        }

        if (!string.IsNullOrWhiteSpace(report.Status))
        {
            ImGui.TextDisabled(
                "UV warning: " + report.Status);
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
            "Experimental UV0 sampling, 7 points/triangle; " +
            "not actual on-screen visibility.");
    }

    private void DrawUvControls()
    {
        ImGui.TextDisabled(
            $"Geometry scan: {uvProcessedModels}/{uvTotalModels} models processed; " +
            $"{pendingUvModels.Count} pending.");
        ImGui.TextDisabled(
            "DEV3: experimental mesh UV0 sampling " +
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
            $"{uvMatchedMeshes} matched meshes, " +
            $"{uvUnmatchedMeshes} unmatched, " +
            $"{uvTriangles} triangles sampled.");
    }

    // ======================================================
    // DIAGNOSTICS
    // ======================================================

    private void AppendUvDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("[EXPERIMENTAL MESH UV REPORT]");

        sb.AppendLine(
            "Mode: UV0/XY, repeating UV, " +
            "7 samples per triangle; " +
            "NOT visible-geometry proof");

        sb.AppendLine("Flip V: " + flipUvV);

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

            sb.AppendLine(
                $"MATERIAL {entry.Key.GamePath} " +
                $"ID {r.Texture}: " +
                $"meshes={r.Meshes} " +
                $"triangles={r.Triangles} " +
                $"samples={r.Samples} " +
                $"clean={r.CleanSamples} " +
                $"offgrid={r.OffGridSamples} " +
                $"skipped={r.SkippedTriangles} " +
                $"capped={r.Capped} " +
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