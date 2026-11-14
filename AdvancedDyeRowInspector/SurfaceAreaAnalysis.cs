using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // Experimental object-space area heuristic.
    //
    // This does NOT account for:
    // - animation/skinning
    // - camera projection
    // - occlusion
    // - backface culling
    // - actual pixels rendered on screen
    //
    // The original UV sample ranking remains unchanged.

    private static Vector3?[] ReadSurfacePositions(
        MdlFile mdl,
        int meshIndex)
    {
        if (meshIndex < 0 ||
            meshIndex >= mdl.Meshes.Length ||
            meshIndex >= mdl.VertexDeclarations.Length)
        {
            throw new InvalidDataException(
                "Position mesh index is invalid.");
        }

        var mesh = mdl.Meshes[meshIndex];
        var declaration = mdl.VertexDeclarations[meshIndex];

        var elements = declaration.VertexElements
            .Where(e => e.Usage == 0 && e.UsageIndex == 0)
            .ToArray();

        if (elements.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one position element, found {elements.Length}.");
        }

        var position = elements[0];

        int elementSize = position.Type switch
        {
            2 => 12, // Float3
            3 => 16, // Float4
            14 => 8, // Half4
            _ => 0
        };

        if (elementSize == 0)
        {
            throw new InvalidDataException(
                $"Unsupported position type {position.Type}.");
        }

        int stream = position.Stream;

        if (stream < 0 ||
            stream >= 3 ||
            stream >= mesh.VertexStreamCount)
        {
            throw new InvalidDataException(
                "Position stream is invalid.");
        }

        int stride = mesh.VertexBufferStride[stream];

        if (stride <= 0 ||
            position.Offset + elementSize > stride)
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
                "Position vertex buffer range is invalid.");
        }

        var result = new Vector3?[mesh.VertexCount];

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            long address = streamStart +
                (long)i * stride + position.Offset;

            if (address < vertexStart ||
                address + elementSize > vertexEnd ||
                address + elementSize > data.LongLength)
            {
                throw new InvalidDataException(
                    "Position data outside vertex buffer.");
            }

            int offset = checked((int)address);

            float x;
            float y;
            float z;

            if (position.Type == 14)
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

            if (float.IsFinite(x) &&
                float.IsFinite(y) &&
                float.IsFinite(z))
            {
                result[i] = new Vector3(x, y, z);
            }
        }

        return result;
    }

    private IEnumerable<(string Row, double Percent)>
        RankedSurfaceRows(UvReport report)
    {
        if (report.SurfaceCleanWeight <= 0)
        {
            return Enumerable.Empty<(string, double)>();
        }

        return report.SurfaceWeights
            .Select((weight, index) => (
                Row:
                    $"{index / 2 + 1}" +
                    (index % 2 == 0 ? "A" : "B"),
                Percent:
                    100.0 * weight / report.SurfaceCleanWeight
            ))
            .Where(x => x.Percent >= minimumUvPercent)
            .OrderByDescending(x => x.Percent);
    }

    private void DrawSurfaceAreaComparison(
        UvReport report,
        int index)
    {
        ImGui.Separator();

        ImGui.TextUnformatted(
            "Alternative: 3D surface-area weighting");

        if (report.SurfaceWeightedTriangles == 0 ||
            report.SurfaceCleanWeight <= 0)
        {
            ImGui.TextDisabled(
                "No usable 3D position-area samples.");

            if (!string.IsNullOrWhiteSpace(report.SurfaceStatus))
                ImGui.TextWrapped(report.SurfaceStatus);

            ImGui.TextDisabled(
                "Original UV results above remain valid.");

            return;
        }

        ImGui.TextDisabled(
            $"3D area available: " +
            $"{report.SurfaceWeightedTriangles:N0}/" +
            $"{report.Triangles:N0} sampled triangles");

        if (report.SurfaceUnavailableTriangles > 0)
        {
            ImGui.TextDisabled(
                "Partial comparison: some triangles have " +
                "missing or degenerate position data.");
        }

        if (!string.IsNullOrWhiteSpace(report.SurfaceStatus))
        {
            ImGui.TextDisabled(report.SurfaceStatus);
        }

        var rows = RankedSurfaceRows(report)
            .Take(8)
            .ToArray();

        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];

            ImGui.TextUnformatted(row.Row);
            ImGui.SameLine();

            ImGui.ProgressBar(
                Math.Clamp(
                    (float)(row.Percent / 100.0),
                    0f, 1f),
                new Vector2(270f, 0f),
                $"{row.Percent:F1}%");
        }

        if (rows.Length > 0 &&
            ImGui.SmallButton($"Copy comparison rows##area_{index}"))
        {
            ImGui.SetClipboardText(
                string.Join(
                    ", ",
                    rows.Select(x => x.Row)));
        }

        ImGui.TextDisabled(
            "Object-space triangle area is NOT visible " +
            "screen area or dye coverage.");
    }
}