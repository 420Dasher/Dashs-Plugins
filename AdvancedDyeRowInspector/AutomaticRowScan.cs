using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private readonly record struct AutoRowCandidate(
        int Index,
        string Row,
        double UvPercent);

    private sealed record AutoRowResult(
        string Row,
        double UvPercent,
        double OuterMedian,
        double OuterMin,
        double OuterMax,
        double CoreMedian,
        double CoreMin,
        double CoreMax,
        double BaselineMean,
        double RestoredMean,
        string Assessment);

    private readonly object autoRowScanGate = new();

    private CancellationTokenSource? autoRowScanCancellation;

    private readonly List<AutoRowResult> autoRowScanResults = new();

    private volatile bool autoRowScanRunning;
    private bool autoRowScanInternalStart;
    private bool autoRowScanDisposed;

    private string autoRowScanStatus = "Not scanned.";
    private string autoRowScanSlot = "";

    private static int ParseAutoRowIndex(string row)
    {
        if (row.Length < 2 ||
            !int.TryParse(row[..^1], out int number) ||
            number < 1 || number > 16)
        {
            return -1;
        }

        char half = char.ToUpperInvariant(row[^1]);

        if (half != 'A' && half != 'B')
            return -1;

        return (number - 1) * 2 +
               (half == 'B' ? 1 : 0);
    }

    private AutoRowCandidate[] GetAutoRowCandidates(
        List<ModelEntry> selectedModels)
    {
        if (!scanned || pendingUvModels.Count > 0)
            return Array.Empty<AutoRowCandidate>();

        var materials = selectedModels
            .SelectMany(model => model.Materials)
            .Where(IsOutfitMaterial)
            .Distinct()
            .ToArray();

        // Only scan materials whose native live texture
        // can be resolved without ambiguous choices.
        if (materials.Length != 1 ||
            !uvReports.TryGetValue(
                materials[0], out var report) ||
            report.CleanSamples <= 0)
        {
            return Array.Empty<AutoRowCandidate>();
        }

        return RankedUvRows(report)
            .Take(5)
            .Select(row => new AutoRowCandidate(
                ParseAutoRowIndex(row.Row),
                row.Row,
                row.Percent))
            .Where(row => row.Index >= 0)
            .ToArray();
    }

    private void SetAutoRowScanStatus(string value)
    {
        lock (autoRowScanGate)
        {
            if (!autoRowScanDisposed)
                autoRowScanStatus = value;
        }
    }

    private static string AssessAutoRowSignal(
        double coreMedian,
        double coreMin,
        double coreMax,
        double baselineMean,
        double restoredMean,
        double baselineStrong)
    {
        double spread = coreMax - coreMin;

        double restorationDrift =
            Math.Abs(restoredMean - baselineMean);

        bool unstable =
            spread > 2.0 ||
            restorationDrift >
                Math.Max(4.0, baselineMean * 0.4) ||
            baselineStrong > 12.0;

        if (unstable)
            return "Uncertain - scene variation";

        if (coreMedian >= 4.0 && coreMin >= 3.0)
            return "Higher relative change signal";

        if (coreMedian <= 2.0 && coreMax <= 3.0)
            return "Low signal - not proof of unused";

        return "Intermediate / uncertain";
    }

    private void BeginAutoRowScan(
        List<ModelEntry> selectedModels)
    {
        if (autoRowScanRunning ||
            controlledRunning ||
            screenNoiseRunning ||
            activeLivePreview.HasValue ||
            pendingLivePreview.HasValue)
        {
            SetAutoRowScanStatus(
                "Another comparison or preview is running.");
            return;
        }

        var candidates = GetAutoRowCandidates(
            selectedModels);

        if (candidates.Length == 0)
        {
            SetAutoRowScanStatus(
                "No uniquely matched UV material or " +
                "eligible ranked rows for this slot.");
            return;
        }

        if (!TryGetActorRegion(out _))
        {
            SetAutoRowScanStatus(
                "Keep the character fully in view.");
            return;
        }

        string slot = compactSlot;
        int previousRow = selectedPreviewRow;

        // Snapshot the selected models and candidate list.
        // A future rescan cancels this operation.
        var snapshot = new List<ModelEntry>(
            selectedModels);

        CancellationTokenSource source;

        lock (autoRowScanGate)
        {
            if (autoRowScanDisposed ||
                autoRowScanRunning)
                return;

            source = new CancellationTokenSource();

            autoRowScanCancellation = source;
            autoRowScanResults.Clear();
            autoRowScanSlot = slot;
            autoRowScanRunning = true;

            autoRowScanStatus =
                $"Starting {candidates.Length} UV-ranked rows...";
        }

        _ = RunAutoRowScanAsync(
            candidates,
            snapshot,
            slot,
            previousRow,
            source);
    }

    private async Task RunAutoRowScanAsync(
        AutoRowCandidate[] candidates,
        List<ModelEntry> selectedModels,
        string originalSlot,
        int previousRow,
        CancellationTokenSource source)
    {
        var token = source.Token;

        try
        {
            // Reuse the existing DEV7 Stage 3 engine.
            // Each call performs three trials and restores
            // the live colorset before it completes.
            for (int i = 0; i < candidates.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                var candidate = candidates[i];

                SetAutoRowScanStatus(
                    $"Row {i + 1}/{candidates.Length}: " +
                    $"{candidate.Row} - three trials...");

                await Framework.RunOnTick(() =>
                {
                    if (!windowOpen ||
                        !scanned ||
                        compactSlot != originalSlot ||
                        controlledRunning ||
                        activeLivePreview.HasValue ||
                        pendingLivePreview.HasValue)
                    {
                        throw new InvalidOperationException(
                            "Selected outfit or preview state changed.");
                    }

                    selectedPreviewRow = candidate.Index;

                    autoRowScanInternalStart = true;

                    try
                    {
                        BeginControlledRowComparison(
                            selectedModels);
                    }
                    finally
                    {
                        autoRowScanInternalStart = false;
                    }

                    if (!controlledRunning)
                    {
                        throw new InvalidOperationException(
                            "Unable to start comparison: " +
                            controlledStatus);
                    }

                }, cancellationToken: token)
                    .ConfigureAwait(false);

                // Wait for the complete comparison,
                // including its framework-thread native
                // texture cleanup, before the next row.
                int polls = 0;

                while (controlledRunning)
                {
                    token.ThrowIfCancellationRequested();

                    if (compactSlot != originalSlot)
                    {
                        CancelControlledRowComparison();

                        throw new InvalidOperationException(
                            "Equipment slot changed during scan.");
                    }

                    if (++polls > 450)
                    {
                        CancelControlledRowComparison();

                        throw new TimeoutException(
                            "A row comparison exceeded the " +
                            "per-candidate safety window.");
                    }

                    await Task.Delay(
                        100, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                ControlledRowResult summary;

                double outerMedian;
                double outerMin;
                double outerMax;

                double coreMedian;
                double coreMin;
                double coreMax;

                lock (controlledGate)
                {
                    if (controlledResult is not { } result ||
                        controlledTrials != 3 ||
                        result.Row != candidate.Row)
                    {
                        throw new InvalidOperationException(
                            "Incomplete comparison for " +
                            candidate.Row + ": " +
                            controlledStatus);
                    }

                    summary = result;

                    outerMedian = controlledOuterMedian;
                    outerMin = controlledOuterMin;
                    outerMax = controlledOuterMax;

                    coreMedian = controlledCoreMedian;
                    coreMin = controlledCoreMin;
                    coreMax = controlledCoreMax;
                }

                string assessment = AssessAutoRowSignal(
                    coreMedian,
                    coreMin,
                    coreMax,
                    summary.BaselineMean,
                    summary.RestoredMean,
                    summary.BaselineStrongPercent);

                var measured = new AutoRowResult(
                    candidate.Row,
                    candidate.UvPercent,
                    outerMedian,
                    outerMin,
                    outerMax,
                    coreMedian,
                    coreMin,
                    coreMax,
                    summary.BaselineMean,
                    summary.RestoredMean,
                    assessment);

                lock (autoRowScanGate)
                {
                    if (!autoRowScanDisposed &&
                        !token.IsCancellationRequested)
                    {
                        autoRowScanResults.Add(measured);
                    }
                }

                if (i + 1 < candidates.Length)
                {
                    await Task.Delay(
                        200, token).ConfigureAwait(false);
                }
            }

            SetAutoRowScanStatus(
                $"Completed {candidates.Length} " +
                "UV-ranked candidates.");
        }
        catch (OperationCanceledException)
        {
            SetAutoRowScanStatus(
                "Automatic candidate scan canceled.");
        }
        catch (Exception ex)
        {
            SetAutoRowScanStatus(
                "Automatic scan stopped: " +
                ex.GetType().Name + ": " + ex.Message);

            Log.Warning(
                $"ADRI automatic candidate scan failed: {ex}");
        }
        finally
        {
            // If cancellation occurs in the middle of a
            // preview, request the existing DEV7 cleanup.
            if (controlledRunning)
                CancelControlledRowComparison();

            // Do not start another scan while a previous
            // native texture restoration is still pending.
            for (int attempt = 0;
                 attempt < 120 && controlledRunning;
                 attempt++)
            {
                await Task.Delay(
                    100).ConfigureAwait(false);
            }

            try
            {
                await Framework.RunOnFrameworkThread(() =>
                {
                    // Return the shared selector to the
                    // user's previous manual selection.
                    selectedPreviewRow = previousRow;

                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    $"ADRI selector restoration failed: {ex}");
            }

            lock (autoRowScanGate)
            {
                if (ReferenceEquals(
                    autoRowScanCancellation, source))
                {
                    autoRowScanCancellation = null;
                    autoRowScanRunning = false;
                }
            }

            source.Dispose();
        }
    }

    private void CancelAutoRowScan()
    {
        CancellationTokenSource? source;

        lock (autoRowScanGate)
            source = autoRowScanCancellation;

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (source != null)
            CancelControlledRowComparison();
    }

    private void DisposeAutoRowScan()
    {
        lock (autoRowScanGate)
            autoRowScanDisposed = true;

        CancelAutoRowScan();
    }

    private void DrawAutoRowScanDiagnostics(
        List<ModelEntry> selectedModels)
    {
        if (!ImGui.TreeNode(
            "DEV7 / automatic candidate scan"))
        {
            return;
        }

        ImGui.TextDisabled(
            "Tests up to five UV-ranked rows automatically.");

        ImGui.TextDisabled(
            "Three controlled comparisons per row; " +
            "restores the colorset between trials.");

        var candidates =
            GetAutoRowCandidates(selectedModels);

        if (!autoRowScanRunning)
        {
            if (candidates.Length == 0)
            {
                ImGui.TextDisabled(
                    "No eligible single-material UV scan.");
            }
            else
            {
                ImGui.TextDisabled(
                    "Candidates: " +
                    string.Join(", ",
                        candidates.Select(x => x.Row)));
            }

            if (candidates.Length > 0 &&
                ImGui.SmallButton(
                    "Scan top UV rows##ADRIAutoStart"))
            {
                BeginAutoRowScan(selectedModels);
            }
        }
        else
        {
            if (ImGui.SmallButton(
                "Cancel automatic scan##ADRIAutoCancel"))
            {
                CancelAutoRowScan();
            }
        }

        string status;
        string slot;
        AutoRowResult[] results;

        lock (autoRowScanGate)
        {
            status = autoRowScanStatus;
            slot = autoRowScanSlot;
            results = autoRowScanResults.ToArray();
        }

        ImGui.TextWrapped(status);

        if (results.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();

            ImGui.TextUnformatted(
                $"Measured {results.Length} candidate(s) " +
                $"for {slot}");

            if (slot != compactSlot)
            {
                ImGui.TextDisabled(
                    "Results belong to another equipment slot.");
            }

            ImGui.TextDisabled(
                "Ranked by character-core median, " +
                "not UV percentage.");

            int rank = 0;

            foreach (var result in results
                .OrderByDescending(r => r.CoreMedian))
            {
                rank++;

                ImGui.TextUnformatted(
                    $"#{rank}  {result.Row}  |  " +
                    $"Core: {result.CoreMedian:F2}%");

                ImGui.TextDisabled(
                    $"UV: {result.UvPercent:F1}%  |  " +
                    $"Core range: {result.CoreMin:F2}-" +
                    $"{result.CoreMax:F2}%");

                ImGui.TextDisabled(
                    $"Outer median: {result.OuterMedian:F2}%  |  " +
                    $"range: {result.OuterMin:F2}-" +
                    $"{result.OuterMax:F2}%");

                ImGui.TextWrapped(result.Assessment);
                ImGui.Spacing();
            }
        }

        ImGui.TextDisabled(
            "Experimental screen-change rankings only.");

        ImGui.TextDisabled(
            "Low signal does not prove a dye row is unused.");

        ImGui.TextDisabled(
            "Current camera angle and scene movement " +
            "can affect the result.");

        ImGui.TreePop();
    }

    private void AppendAutoRowScanDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine(
            "[DEV7 STAGE 4 AUTOMATIC CANDIDATE SCAN]");

        lock (autoRowScanGate)
        {
            sb.AppendLine(
                "Status: " + autoRowScanStatus);

            sb.AppendLine(
                "Slot: " + autoRowScanSlot);

            sb.AppendLine(
                "Running: " + autoRowScanRunning);

            foreach (var row in autoRowScanResults)
            {
                sb.AppendLine(
                    $"{row.Row}: UV {row.UvPercent:F3}%; " +
                    $"core {row.CoreMedian:F3}% " +
                    $"[{row.CoreMin:F3}, {row.CoreMax:F3}]; " +
                    $"outer {row.OuterMedian:F3}% " +
                    $"[{row.OuterMin:F3}, {row.OuterMax:F3}]; " +
                    $"baseline {row.BaselineMean:F3}; " +
                    $"restored {row.RestoredMean:F3}; " +
                    row.Assessment);
            }
        }

        sb.AppendLine();
    }
}