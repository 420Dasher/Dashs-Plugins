using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private sealed class SpatialMeshBounds
    {
        public int Index;
        public string Material = "";
        public Vector3 Min;
        public Vector3 Max;
        public Vector3 Center;
        public int UsedVertices;
        public int InvalidReferences;
        public int InvalidPositions;
        public bool HasBounds;

        public bool Complete =>
            HasBounds &&
            InvalidReferences == 0 &&
            InvalidPositions == 0;

        public double Volume
        {
            get
            {
                if (!HasBounds)
                    return 0;

                Vector3 size = Max - Min;

                return (double)size.X * size.Y * size.Z;
            }
        }
    }

    private readonly List<string> spatialGeometryLines = new();
    private string spatialGeometryStatus = "Not checked.";
    private int spatialGeometryOmitted;

    private const int MaxSpatialLines = 256;
    private const int MaxSpatialMeshesPerModel = 24;
    private const int MaxSpatialPairChecks = 128;

    private void ResetSpatialGeometryAudit()
    {
        spatialGeometryLines.Clear();
        spatialGeometryStatus = "Not checked.";
        spatialGeometryOmitted = 0;
    }

    private void AddSpatialLine(string line)
    {
        if (spatialGeometryLines.Count >= MaxSpatialLines)
        {
            spatialGeometryOmitted++;
            return;
        }

        spatialGeometryLines.Add(line);
    }

    private static string SpatialVector(Vector3 v)
    {
        return "(" +
            v.X.ToString("F3", CultureInfo.InvariantCulture) + ", " +
            v.Y.ToString("F3", CultureInfo.InvariantCulture) + ", " +
            v.Z.ToString("F3", CultureInfo.InvariantCulture) + ")";
    }

    private static SpatialMeshBounds ReadSpatialBounds(
        MdlFile mdl,
        int meshIndex)
    {
        if (meshIndex < 0 ||
            meshIndex >= mdl.Meshes.Length ||
            meshIndex >= mdl.VertexDeclarations.Length)
        {
            throw new InvalidDataException(
                "Invalid mesh or vertex-declaration index.");
        }

        var mesh = mdl.Meshes[meshIndex];

        var result = new SpatialMeshBounds
        {
            Index = meshIndex,
            Material = ReadMaterialName(
                mdl, mesh.MaterialIndex)
        };

        if (mesh.VertexCount == 0 || mesh.IndexCount == 0)
            return result;

        if (mesh.VertexCount > 65536 ||
            mesh.IndexCount > 240000)
        {
            throw new InvalidDataException(
                "Spatial mesh safety limit exceeded.");
        }

        var elements = mdl.VertexDeclarations[meshIndex]
            .VertexElements
            .Where(e => e.Usage == 0 && e.UsageIndex == 0)
            .ToArray();

        if (elements.Length != 1)
        {
            throw new InvalidDataException(
                "Expected exactly one position element.");
        }

        var element = elements[0];

        // FF XIV position element formats:
        // 2=Single3, 3=Single4, 14=Half4.
        int size = element.Type switch
        {
            2 => 12,
            3 => 16,
            14 => 8,
            _ => 0
        };

        if (size == 0)
        {
            throw new InvalidDataException(
                $"Unsupported position type {element.Type}.");
        }

        int stream = element.Stream;

        if (stream >= 3 || stream >= mesh.VertexStreamCount)
        {
            throw new InvalidDataException(
                "Invalid position stream.");
        }

        int stride = mesh.VertexBufferStride[stream];

        if (stride <= 0 || element.Offset + size > stride)
        {
            throw new InvalidDataException(
                "Invalid position stride or offset.");
        }

        byte[] data = mdl.Data;

        long vertexStart = mdl.FileHeader.VertexOffset[0];
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
                "Position buffer range invalid.");
        }

        long indexBase = mdl.FileHeader.IndexOffset[0];
        long indexEnd = indexBase +
            mdl.FileHeader.IndexBufferSize[0];

        long indexStart = indexBase +
            (long)mesh.StartIndex * 2;

        long requiredEnd = indexStart +
            (long)mesh.IndexCount * 2;

        if (indexStart < indexBase ||
            requiredEnd > indexEnd ||
            requiredEnd > data.LongLength)
        {
            throw new InvalidDataException(
                "Position index buffer range invalid.");
        }

        var referenced = new HashSet<ushort>();
        int vertexCount = mesh.VertexCount;
        int indexCount = checked((int)mesh.IndexCount);

        for (int i = 0; i < indexCount; i++)
        {
            ushort vertex = BitConverter.ToUInt16(
                data,
                checked((int)(indexStart + (long)i * 2)));

            if (vertex >= vertexCount)
            {
                result.InvalidReferences++;
                continue;
            }

            referenced.Add(vertex);
        }

        Vector3 min = new(
            float.PositiveInfinity,
            float.PositiveInfinity,
            float.PositiveInfinity);

        Vector3 max = new(
            float.NegativeInfinity,
            float.NegativeInfinity,
            float.NegativeInfinity);

        double sumX = 0;
        double sumY = 0;
        double sumZ = 0;

        foreach (ushort vertex in referenced)
        {
            long address = streamStart +
                (long)vertex * stride + element.Offset;

            if (address < vertexStart ||
                address + size > vertexEnd ||
                address + size > data.LongLength)
            {
                result.InvalidPositions++;
                continue;
            }

            int offset = checked((int)address);

            float x;
            float y;
            float z;

            if (element.Type == 14)
            {
                x = (float)BitConverter.UInt16BitsToHalf(
                    BitConverter.ToUInt16(data, offset));

                y = (float)BitConverter.UInt16BitsToHalf(
                    BitConverter.ToUInt16(data, offset + 2));

                z = (float)BitConverter.UInt16BitsToHalf(
                    BitConverter.ToUInt16(data, offset + 4));
            }
            else
            {
                x = BitConverter.ToSingle(data, offset);
                y = BitConverter.ToSingle(data, offset + 4);
                z = BitConverter.ToSingle(data, offset + 8);
            }

            if (!float.IsFinite(x) ||
                !float.IsFinite(y) ||
                !float.IsFinite(z) ||
                MathF.Abs(x) > 100000f ||
                MathF.Abs(y) > 100000f ||
                MathF.Abs(z) > 100000f)
            {
                result.InvalidPositions++;
                continue;
            }

            var position = new Vector3(x, y, z);

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);

            sumX += x;
            sumY += y;
            sumZ += z;

            result.UsedVertices++;
        }

        if (result.UsedVertices == 0)
            return result;

        result.HasBounds = true;
        result.Min = min;
        result.Max = max;

        result.Center = new Vector3(
            (float)(sumX / result.UsedVertices),
            (float)(sumY / result.UsedVertices),
            (float)(sumZ / result.UsedVertices));

        return result;
    }

    private static double SpatialIntersectionVolume(
        SpatialMeshBounds a,
        SpatialMeshBounds b)
    {
        if (!a.Complete || !b.Complete)
            return 0;

        float x = MathF.Max(0,
            MathF.Min(a.Max.X, b.Max.X) -
            MathF.Max(a.Min.X, b.Min.X));

        float y = MathF.Max(0,
            MathF.Min(a.Max.Y, b.Max.Y) -
            MathF.Max(a.Min.Y, b.Min.Y));

        float z = MathF.Max(0,
            MathF.Min(a.Max.Z, b.Max.Z) -
            MathF.Max(a.Min.Z, b.Min.Z));

        return (double)x * y * z;
    }

    private void CaptureSpatialGeometry(
        List<ModelEntry> selectedModels)
    {
        ResetSpatialGeometryAudit();

        if (!scanned || pendingUvModels.Count > 0)
        {
            spatialGeometryStatus =
                "Scan outfit and finish geometry analysis first.";
            return;
        }

        var actor = Objects.LocalPlayer;

        if (actor == null ||
            actor.Name.TextValue != actorName ||
            actor.ObjectIndex.ToString() != actorIndex)
        {
            spatialGeometryStatus =
                "Character changed; rescan the outfit.";
            return;
        }

        int processedModels = 0;
        int completedMeshes = 0;
        int overlappingPairs = 0;

        // The selected equipment slot only.
        foreach (var model in selectedModels
            .Where(m => m.IsOutfit)
            .Take(12))
        {
            AddSpatialLine("MODEL: " + model.GamePath);

            try
            {
                MdlFile? mdl = LoadModelFile(model);

                if (mdl == null ||
                    mdl.Data == null ||
                    mdl.Data.Length > 64L * 1024 * 1024 ||
                    mdl.Lods == null ||
                    mdl.Lods.Length == 0 ||
                    mdl.Meshes == null)
                {
                    AddSpatialLine(
                        "  Model data unavailable or exceeds safety limit.");
                    continue;
                }

                int first = mdl.Lods[0].MeshIndex;
                int end = first + mdl.Lods[0].MeshCount;

                if (first < 0 ||
                    end < first ||
                    end > mdl.Meshes.Length)
                {
                    AddSpatialLine(
                        "  Invalid LOD0 mesh range.");
                    continue;
                }

                processedModels++;

                AddSpatialLine(
                    "  Coordinates: static model-local, " +
                    "index-referenced vertices only. " +
                    "No skeletal pose or camera transform.");

                var bounds = new List<SpatialMeshBounds>();

                int limit = Math.Min(
                    end, first + MaxSpatialMeshesPerModel);

                if (limit < end)
                {
                    AddSpatialLine(
                        $"  Mesh safety limit: {end - limit} " +
                        "additional mesh records not analyzed.");
                }

                for (int meshIndex = first;
                     meshIndex < limit;
                     meshIndex++)
                {
                    try
                    {
                        var result = ReadSpatialBounds(
                            mdl, meshIndex);

                        bounds.Add(result);

                        if (!result.HasBounds)
                        {
                            AddSpatialLine(
                                $"  Mesh {meshIndex} / {result.Material}: " +
                                "no usable position bounds.");
                            continue;
                        }

                        if (result.Complete)
                            completedMeshes++;

                        AddSpatialLine(
                            $"  Mesh {meshIndex} / {result.Material}: " +
                            $"vertices={result.UsedVertices}, " +
                            $"complete={result.Complete}, " +
                            $"invalidIndices={result.InvalidReferences}, " +
                            $"invalidPositions={result.InvalidPositions}");

                        AddSpatialLine(
                            "    min=" + SpatialVector(result.Min) +
                            " max=" + SpatialVector(result.Max));

                        AddSpatialLine(
                            "    size=" +
                            SpatialVector(result.Max - result.Min) +
                            " centroid=" +
                            SpatialVector(result.Center));
                    }
                    catch (Exception ex)
                    {
                        AddSpatialLine(
                            $"  Mesh {meshIndex}: " +
                            "position parsing unavailable (" +
                            ex.GetType().Name + ": " +
                            ex.Message + ").");
                    }
                }

                int pairChecks = 0;
                int modelOverlaps = 0;
                bool pairLimitReached = false;

                for (int i = 0; i < bounds.Count; i++)
                {
                    for (int j = i + 1; j < bounds.Count; j++)
                    {
                        var a = bounds[i];
                        var b = bounds[j];

                        if (!a.Complete || !b.Complete ||
                            a.Volume <= 0 || b.Volume <= 0)
                            continue;

                        if (pairChecks >= MaxSpatialPairChecks)
                        {
                            pairLimitReached = true;
                            break;
                        }

                        pairChecks++;

                        double overlap =
                            SpatialIntersectionVolume(a, b);

                        if (overlap <= 0)
                            continue;

                        modelOverlaps++;
                        overlappingPairs++;

                        double percent = 100.0 * overlap /
                            Math.Min(a.Volume, b.Volume);

                        AddSpatialLine(
                            $"  AABB pair: meshes {a.Index} / {b.Index}, " +
                            $"intersection={overlap:F4}, " +
                            $"intersection/smaller-box={percent:F2}%");
                    }

                    if (pairLimitReached)
                        break;
                }

                AddSpatialLine(
                    $"  Pairwise check: {pairChecks} comparisons, " +
                    $"{modelOverlaps} overlapping bounding-box pairs." +
                    (pairLimitReached
                        ? " Comparison safety limit reached."
                        : ""));
            }
            catch (Exception ex)
            {
                AddSpatialLine(
                    "  Spatial audit unavailable: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        spatialGeometryStatus =
            $"Model files {processedModels}; " +
            $"complete mesh bounds {completedMeshes}; " +
            $"AABB overlaps {overlappingPairs}.";

        if (processedModels == 0)
        {
            spatialGeometryStatus =
                "No usable spatial model data found.";
        }
    }

    private void DrawSpatialGeometryAudit(
        List<ModelEntry> selectedModels)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(
            "Read-only spatial geometry");

        if (ImGui.SmallButton(
            "Check spatial geometry##ADRISpatial"))
        {
            CaptureSpatialGeometry(selectedModels);
        }

        ImGui.TextDisabled(spatialGeometryStatus);

        ImGui.TextDisabled(
            "Static model-local AABBs; overlap does not " +
            "prove occlusion or rendered visibility.");

        if (spatialGeometryLines.Count == 0)
            return;

        if (ImGui.TreeNode(
            $"Spatial details ({spatialGeometryLines.Count})"))
        {
            foreach (string line in spatialGeometryLines)
                ImGui.TextWrapped(line);

            if (spatialGeometryOmitted > 0)
            {
                ImGui.TextDisabled(
                    $"{spatialGeometryOmitted} lines omitted.");
            }

            ImGui.TreePop();
        }
    }

    private void AppendSpatialGeometryDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine(
            "[DEV8 STAGE 2G SPATIAL GEOMETRY AUDIT]");

        sb.AppendLine(spatialGeometryStatus);

        sb.AppendLine(
            "Static index-referenced positions only; " +
            "AABB overlap is NOT rendered visibility, " +
            "pixel occlusion or posed geometry.");

        foreach (string line in spatialGeometryLines)
            sb.AppendLine(line);

        if (spatialGeometryOmitted > 0)
        {
            sb.AppendLine(
                $"Diagnostic lines omitted: {spatialGeometryOmitted}");
        }

        sb.AppendLine();
    }
}