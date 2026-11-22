using System;
using System.Numerics;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // Fixed grid for experimental UV texture coverage.
    // Each cell represents an area of UV space, not
    // measured physical screen-space garment area.
    private const int UvMaskSize = 256;

    private const int MaxUvRasterChecks = 12_000_000;

    private int uvRasterChecks;
    private bool uvRasterBudgetExceeded;
    private bool uvTriangleLimitReached;

    private void ResetRasterCounters()
    {
        uvRasterChecks = 0;
        uvRasterBudgetExceeded = false;
        uvTriangleLimitReached = false;
    }

    private void MarkUvReportsPartial()
    {
        foreach (var report in uvReports.Values)
            report.Capped = true;
    }

    private void RasterizeMesh(
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
                uvTriangleLimitReached = true;
                MarkUvReportsPartial();
                return;
            }

            if (uvRasterBudgetExceeded)
            {
                MarkUvReportsPartial();
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

            RasterizeTriangle(
                vertices[i0]!.Value,
                vertices[i1]!.Value,
                vertices[i2]!.Value,
                tex,
                report);

            report.Triangles++;
            uvTriangles++;

            if (uvRasterBudgetExceeded)
            {
                MarkUvReportsPartial();
                return;
            }
        }
    }

    private void RasterizeTriangle(
        Vector2 a,
        Vector2 b,
        Vector2 c,
        TexturePixels tex,
        UvReport report)
    {
        // Rasterize in unwrapped UV space so that
        // triangles crossing integer tile boundaries
        // naturally contribute to both repeated tiles.
        //
        // A V flip changes the sign of V. This preserves
        // repeating UV behavior and avoids double flipping
        // at integer texture coordinates.

        double ax = a.X * UvMaskSize;
        double bx = b.X * UvMaskSize;
        double cx = c.X * UvMaskSize;

        double vSign = flipUvV ? -1.0 : 1.0;

        double ay = a.Y * vSign * UvMaskSize;
        double by = b.Y * vSign * UvMaskSize;
        double cy = c.Y * vSign * UvMaskSize;

        double minX = Math.Min(ax, Math.Min(bx, cx));
        double maxX = Math.Max(ax, Math.Max(bx, cx));
        double minY = Math.Min(ay, Math.Min(by, cy));
        double maxY = Math.Max(ay, Math.Max(by, cy));

        // Fail visibly rather than perform potentially
        // unbounded work on malformed or extreme UVs.
        if (!double.IsFinite(minX) ||
            !double.IsFinite(maxX) ||
            !double.IsFinite(minY) ||
            !double.IsFinite(maxY) ||
            Math.Abs(minX) > 2048 * UvMaskSize ||
            Math.Abs(maxX) > 2048 * UvMaskSize ||
            Math.Abs(minY) > 2048 * UvMaskSize ||
            Math.Abs(maxY) > 2048 * UvMaskSize ||
            maxX - minX > 4 * UvMaskSize ||
            maxY - minY > 4 * UvMaskSize)
        {
            report.SkippedTriangles++;
            report.Capped = true;
            report.Status =
                "Some UV triangles exceed the safe tiling range.";
            return;
        }

        double area =
            (bx - ax) * (cy - ay) -
            (by - ay) * (cx - ax);

        if (Math.Abs(area) <= 1e-8)
        {
            report.SkippedTriangles++;
            return;
        }

        int tileX0 = (int)Math.Floor(
            minX / UvMaskSize);

        int tileX1 = (int)Math.Floor(
            maxX / UvMaskSize);

        int tileY0 = (int)Math.Floor(
            minY / UvMaskSize);

        int tileY1 = (int)Math.Floor(
            maxY / UvMaskSize);

        for (int tileY = tileY0; tileY <= tileY1; tileY++)
        {
            double shiftY = (double)tileY * UvMaskSize;

            int y0 = Math.Max(
                0,
                (int)Math.Ceiling(
                    minY - shiftY - 0.5));

            int y1 = Math.Min(
                UvMaskSize - 1,
                (int)Math.Floor(
                    maxY - shiftY - 0.5));

            if (y1 < y0)
                continue;

            for (int tileX = tileX0; tileX <= tileX1; tileX++)
            {
                double shiftX =
                    (double)tileX * UvMaskSize;

                int x0 = Math.Max(
                    0,
                    (int)Math.Ceiling(
                        minX - shiftX - 0.5));

                int x1 = Math.Min(
                    UvMaskSize - 1,
                    (int)Math.Floor(
                        maxX - shiftX - 0.5));

                if (x1 < x0)
                    continue;

                for (int y = y0; y <= y1; y++)
                {
                    double py = shiftY + y + 0.5;

                    for (int x = x0; x <= x1; x++)
                    {
                        if (++uvRasterChecks >
                            MaxUvRasterChecks)
                        {
                            uvRasterBudgetExceeded = true;
                            report.Capped = true;
                            report.Status =
                                "UV raster work budget exceeded.";
                            return;
                        }

                        double px = shiftX + x + 0.5;

                        double e0 =
                            (bx - ax) * (py - ay) -
                            (by - ay) * (px - ax);

                        double e1 =
                            (cx - bx) * (py - by) -
                            (cy - by) * (px - bx);

                        double e2 =
                            (ax - cx) * (py - cy) -
                            (ay - cy) * (px - cx);

                        const double epsilon = 1e-8;

                        bool inside = area > 0
                            ? e0 >= -epsilon &&
                              e1 >= -epsilon &&
                              e2 >= -epsilon
                            : e0 <= epsilon &&
                              e1 <= epsilon &&
                              e2 <= epsilon;

                        if (!inside)
                            continue;

                        int cell = y * UvMaskSize + x;

                        // Multiple triangles, or repeated
                        // texture tiles, must not inflate
                        // this material's coverage.
                        if (report.Mask[cell] != 0)
                            continue;

                        report.Mask[cell] = 1;
                        report.Samples++;

                        // Point sample the decoded original
                        // texture rather than filtered GPU
                        // or mipmapped image data.
                        int texX = Math.Clamp(
                            (int)((x + 0.5) *
                                tex.Width / UvMaskSize),
                            0,
                            tex.Width - 1);

                        int texY = Math.Clamp(
                            (int)((y + 0.5) *
                                tex.Height / UvMaskSize),
                            0,
                            tex.Height - 1);

                        int offset =
                            (texY * tex.Width + texX) * 4;

                        byte red = tex.Bgra[offset + 2];
                        byte green = tex.Bgra[offset + 1];

                        int pair = Math.Clamp(
                            (red + 8) / 17,
                            0,
                            15);

                        // Preserve the existing conservative
                        // off-grid filter for compressed maps.
                        if (Math.Abs(
                            red - pair * 17) > 3)
                        {
                            report.OffGridSamples++;
                            continue;
                        }

                        report.CleanSamples++;

                        // Dawntrail row pair A/B contribution:
                        // green near 255 favors A,
                        // green near 0 favors B.
                        // Intermediate green contributes to
                        // both rather than inventing a hard
                        // binary classification.
                        report.Weights[pair * 2] +=
                            green / 255.0;

                        report.Weights[pair * 2 + 1] +=
                            (255 - green) / 255.0;
                    }
                }
            }
        }
    }
}
