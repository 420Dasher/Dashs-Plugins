using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // Never interpret these limits as rendering evidence.
    // Every model gets a reserved, bounded UV allowance.
    private const int MaxUvTotalTriangleAttempts = 300_000;
    private const int MaxUvModelTriangleAttempts = 60_000;
    private const int MaxUvRasterChecksPerModel = 1_000_000;

    private int uvTriangleAttempts;
    private int uvModelTriangleAttempts;
    private int uvModelTriangleBudget;
    private int uvModelRasterChecks;
    private int uvModelRasterBudget;
    private int uvModelBudgetStops;
    private int uvKnownNonOutfitMeshes;
    private int uvEmptyMeshes;
    private int uvRaceVariantLinks;

    private bool uvModelRasterBudgetExceeded;
    private bool uvModelBudgetReported;

    private readonly HashSet<MaterialEntry>
        uvRaceVariantMaterials = new();

    private static string PluginBuildVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    // This is a structural name comparison, not a
    // claim that two materials are the same resource.
    private static readonly Regex RaceMaterialPrefix = new(
        @"(?<=_)c[0-9]{4}",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    private static bool IsRaceVariantName(
        string modelName,
        string linkedName)
    {
        if (string.Equals(
            modelName, linkedName,
            StringComparison.OrdinalIgnoreCase))
            return false;

        if (RaceMaterialPrefix.Matches(modelName).Count != 1 ||
            RaceMaterialPrefix.Matches(linkedName).Count != 1)
            return false;

        string a = RaceMaterialPrefix.Replace(
            modelName, "c####");
        string b = RaceMaterialPrefix.Replace(
            linkedName, "c####");

        return string.Equals(
            a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void ResetUvFairness()
    {
        uvTriangleAttempts = 0;
        uvModelTriangleAttempts = 0;
        uvModelRasterChecks = 0;
        uvModelBudgetStops = 0;
        uvKnownNonOutfitMeshes = 0;
        uvEmptyMeshes = 0;
        uvRaceVariantLinks = 0;
        uvModelRasterBudgetExceeded = false;
        uvModelBudgetReported = false;
        uvRaceVariantMaterials.Clear();
    }

    private void BeginUvModelBudget()
    {
        int count = Math.Max(1, uvTotalModels);

        uvModelTriangleBudget = Math.Max(
            1, Math.Min(
                MaxUvModelTriangleAttempts,
                MaxUvTotalTriangleAttempts / count));

        uvModelRasterBudget = Math.Max(
            1, Math.Min(
                MaxUvRasterChecksPerModel,
                MaxUvRasterChecks / count));

        uvModelTriangleAttempts = 0;
        uvModelRasterChecks = 0;
        uvModelRasterBudgetExceeded = false;
        uvModelBudgetReported = false;
    }

    private bool HasUvBudget()
    {
        return uvTriangleAttempts < MaxUvTotalTriangleAttempts &&
            uvModelTriangleAttempts < uvModelTriangleBudget &&
            uvRasterChecks < MaxUvRasterChecks &&
            uvModelRasterChecks < uvModelRasterBudget &&
            !uvRasterBudgetExceeded &&
            !uvModelRasterBudgetExceeded;
    }

    private void FlagUvBudgetStop()
    {
        if (uvTriangleAttempts >= MaxUvTotalTriangleAttempts)
            uvTriangleLimitReached = true;

        if (uvRasterChecks >= MaxUvRasterChecks)
            uvRasterBudgetExceeded = true;

        if (uvModelRasterChecks >= uvModelRasterBudget)
            uvModelRasterBudgetExceeded = true;

        if (!uvModelBudgetReported)
        {
            uvModelBudgetReported = true;
            uvModelBudgetStops++;
        }
    }

    private string DescribeUvBudgetStop()
    {
        if (uvRasterChecks >= MaxUvRasterChecks)
            return "Global raster-check budget reached.";

        if (uvModelRasterChecks >= uvModelRasterBudget ||
            uvModelRasterBudgetExceeded)
            return "Per-model raster-check budget reached.";

        if (uvTriangleAttempts >= MaxUvTotalTriangleAttempts)
            return "Global triangle-attempt budget reached.";

        if (uvModelTriangleAttempts >= uvModelTriangleBudget)
            return "Per-model triangle-attempt budget reached.";

        return "UV sampling budget interrupted.";
    }
}