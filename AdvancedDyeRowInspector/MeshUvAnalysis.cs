using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;
using Lumina.Models.Models;

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

            return Data.GameData.GetFileFromDisk<MdlFile>(
                actual, game);
        }

        return Data.GetFile<MdlFile>(
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

    private void AnalyzeGeometry()
    {
        uvReports.Clear();
        uvModelStatus.Clear();

        uvMatchedModels = 0;
        uvMatchedMeshes = 0;
        uvUnmatchedMeshes = 0;
        uvTriangles = 0;

        long cacheBytes = 0;

        var images = new Dictionary<string, TexturePixels>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var model in models.Where(x => x.IsOutfit))
            {
                try
                {
                    var mdl = LoadModelFile(model);

                    if (mdl == null)
                    {
                        uvModelStatus[model] = "MDL unavailable";
                        continue;
                    }

                    if (mdl.Data.Length > 64L * 1024 * 1024)
                    {
                        uvModelStatus[model] =
                            "MDL exceeds safety limit";
                        continue;
                    }

                    var parsed = new Model(
                        mdl, Model.ModelLod.High);

                    if (parsed.Meshes == null)
                    {
                        uvModelStatus[model] = "No parsed meshes";
                        continue;
                    }

                    int linked = 0;
                    int unmatched = 0;

                    foreach (var mesh in parsed.Meshes)
                    {
                        if (!mesh.Types.Contains(Mesh.MeshType.Main))
                            continue;

                        string meshMatName = BaseName(
                            mesh.Material.MaterialPath ?? "");

                        var matches = model.Materials
                            .Where(IsOutfitMaterial)
                            .Where(m =>
                                string.Equals(
                                    BaseName(m.GamePath),
                                    meshMatName,
                                    StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (matches.Count != 1)
                        {
                            unmatched++;
                            uvUnmatchedMeshes++;
                            continue;
                        }

                        var material = matches[0];

                        var ids = material.Textures
                            .Where(t =>
                                t.IsId &&
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
                            unmatched++;
                            uvUnmatchedMeshes++;
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
                                ids[0], images, ref cacheBytes);

                            SampleMesh(mesh, pixels, report);

                            linked++;
                            report.Meshes++;
                            uvMatchedMeshes++;
                        }
                        catch (Exception ex)
                        {
                            report.Status = ex.Message;
                            unmatched++;
                            uvUnmatchedMeshes++;
                        }
                    }

                    if (linked > 0)
                        uvMatchedModels++;

                    uvModelStatus[model] =
                        $"{linked} mesh(es) matched; " +
                        $"{unmatched} unmatched/skipped";
                }
                catch (Exception ex)
                {
                    uvModelStatus[model] =
                        "Parse failed: " +
                        ex.GetType().Name +
                        ": " + ex.Message;

                    Log.Warning(
                        $"ADRI DEV3 model parse failed: {ex}");
                }
            }
        }
        finally
        {
            images.Clear();
        }
    }

    private void SampleMesh(
        Mesh mesh,
        TexturePixels tex,
        UvReport report)
    {
        if (mesh.Vertices == null ||
            mesh.Indices == null)
        {
            throw new InvalidOperationException(
                "Mesh vertices or indices unavailable.");
        }

        var indices = mesh.Indices;
        var vertices = mesh.Vertices;

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
                !vertices[i0].UV.HasValue ||
                !vertices[i1].UV.HasValue ||
                !vertices[i2].UV.HasValue)
            {
                report.SkippedTriangles++;
                continue;
            }

            Vector4 a = vertices[i0].UV!.Value;
            Vector4 b = vertices[i1].UV!.Value;
            Vector4 c = vertices[i2].UV!.Value;

            if (!float.IsFinite(a.X) ||
                !float.IsFinite(a.Y) ||
                !float.IsFinite(b.X) ||
                !float.IsFinite(b.Y) ||
                !float.IsFinite(c.X) ||
                !float.IsFinite(c.Y))
            {
                report.SkippedTriangles++;
                continue;
            }

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