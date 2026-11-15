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
        double ControlMedian,
        double NetSignal,
        int Retries,
        int UnstableTrials,
        double BaselineStrongPercent,
        string Assessment);

    private readonly object autoRowScanGate = new();

    private CancellationTokenSource? autoRowScanCancellation;

    private readonly List<AutoRowResult> autoRowScanResults = new();

    private volatile bool autoRowScanRunning;
    private bool autoRowScanInternalStart;
    private bool autoRowScanDisposed;

    private string autoRowScanStatus = "Not scanned.";
    private string autoRowScanSlot = "";

    // Results are valid only for the scan that made them.
    private DateTime autoRowScanSourceTime;

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
        double baselineStrong,
        double controlMedian,
        int retries,
        int unstableTrials)
    {
        double spread = coreMax - coreMin;
        double drift =
            Math.Abs(restoredMean - baselineMean);

        double netSignal =
            Math.Max(0.0, coreMedian - controlMedian);

        bool unstable =
            unstableTrials > 0 ||
            spread > 2.5 ||
            controlMedian > 3.0 ||
            drift > Math.Max(4.0, baselineMean * 0.4) ||
            baselineStrong > 12.0;

        if (unstable)
            return "Inconclusive - control or scene noise";

        if (netSignal >= 3.0 && coreMin >= 3.0)
            return "Strong comparative signal (experimental)";

        if (netSignal >= 1.5)
            return "Possible visible effect";

        return "Low relative signal - not proof of unused";
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
            autoRowScanSourceTime = scanTime;
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

                double controlMedian;
                int retries;
                int unstableTrials;

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

                    controlMedian = controlledCoreControlMedian;
                    retries = controlledRejectedTrials;
                    unstableTrials = controlledUnstableTrials;
                }

                string assessment = AssessAutoRowSignal(
                    coreMedian,
                    coreMin,
                    coreMax,
                    summary.BaselineMean,
                    summary.RestoredMean,
                    summary.BaselineStrongPercent,
                    controlMedian,
                    retries,
                    unstableTrials);

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
                    controlMedian,
                    Math.Max(0.0, coreMedian - controlMedian),
                    retries,
                    unstableTrials,
                    summary.BaselineStrongPercent,
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

    private AutoRowResult[] CurrentCompactAutoResults()
    {
        lock (autoRowScanGate)
        {
            if (autoRowScanSourceTime != scanTime ||
                autoRowScanSlot != compactSlot)
            {
                return Array.Empty<AutoRowResult>();
            }

            return autoRowScanResults
                .OrderByDescending(r => r.NetSignal)
                .ToArray();
        }
    }

    private bool HasCompactAutoResults()
    {
        lock (autoRowScanGate)
        {
            return autoRowScanSourceTime == scanTime &&
                   autoRowScanSlot == compactSlot &&
                   autoRowScanResults.Count > 0;
        }
    }

    private static string CompactSignalAssessment(
        AutoRowResult result)
    {
        // Preserve the actual underlying assessment.
        // A large numeric signal is NOT automatically
        // reliable if the scanner found scene instability.
        if (result.Assessment.StartsWith(
            "Inconclusive",
            StringComparison.OrdinalIgnoreCase))
        {
            return result.NetSignal >= 3.0
                ? "High signal - inconclusive"
                : "Inconclusive - scene noise";
        }

        if (result.Assessment.StartsWith(
            "Strong",
            StringComparison.OrdinalIgnoreCase))
        {
            return "Strong comparative signal";
        }

        if (result.Assessment.StartsWith(
            "Possible",
            StringComparison.OrdinalIgnoreCase))
        {
            return "Possible visible effect";
        }

        return "Low observed change";
    }

    private static string CompactSignalReason(
        AutoRowResult result)
    {
        var reasons = new List<string>();

        if (result.UnstableTrials > 0)
        {
            reasons.Add(
                "unstable trial after retry limit");
        }

        if (result.CoreMax - result.CoreMin > 2.5)
        {
            reasons.Add(
                "large variation between trials");
        }

        if (result.ControlMedian > 3.0)
        {
            reasons.Add(
                "high no-change control");
        }

        double drift = Math.Abs(
            result.RestoredMean - result.BaselineMean);

        if (drift > Math.Max(
            4.0, result.BaselineMean * 0.4))
        {
            reasons.Add(
                "scene drift after restoration");
        }

        if (result.BaselineStrongPercent > 12.0)
        {
            reasons.Add(
                "high natural scene movement");
        }

        if (reasons.Count == 0)
        {
            return "Measurement reliability was limited.";
        }

        return "Uncertain because of " +
            string.Join("; ", reasons) + ".";
    }

    private void DrawCompactAutoScan(
        List<ModelEntry> selectedModels)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Visual row check");

        var candidates =
            GetAutoRowCandidates(selectedModels);

        if (autoRowScanRunning)
        {
            if (ImGui.SmallButton(
                "Cancel row scan##ADRICompactAutoCancel"))
            {
                CancelAutoRowScan();
            }
        }
        else if (candidates.Length > 0)
        {
            if (ImGui.SmallButton(
                "Check top 5 rows##ADRICompactAutoStart"))
            {
                BeginAutoRowScan(selectedModels);
            }
        }
        else
        {
            ImGui.TextDisabled(
                "No uniquely matched UV candidates available.");
        }

        string message;
        bool belongsToCurrentScan;

        lock (autoRowScanGate)
        {
            belongsToCurrentScan =
                autoRowScanSourceTime == scanTime &&
                autoRowScanSlot == compactSlot;

            message = autoRowScanStatus;
        }

        if (belongsToCurrentScan)
        {
            ImGui.TextDisabled(message);
        }
        else
        {
            ImGui.TextDisabled(
                "Not checked for this outfit scan.");
        }

        var results = CurrentCompactAutoResults();

        if (results.Length == 0)
        {
            ImGui.TextDisabled(
                "Checks current-camera screen changes " +
                "against a no-change control.");

            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Visual signal above control | " +
            $"{results.Length} row(s) measured");

        double largest = Math.Max(
            0.001,
            results.Max(r => r.NetSignal));

        foreach (var result in results)
        {
            ImGui.TextUnformatted(result.Row);
            ImGui.SameLine();

            float width = Math.Clamp(
                ImGui.GetContentRegionAvail().X - 105f,
                110f,
                215f);

            ImGui.ProgressBar(
                (float)Math.Clamp(
                    result.NetSignal / largest,
                    0.0,
                    1.0),
                new System.Numerics.Vector2(width, 0f),
                $"{result.NetSignal:F2}%");

            ImGui.SameLine();
            ImGui.TextDisabled(
                $"UV {result.UvPercent:F1}%");

            ImGui.TextDisabled(
                CompactSignalAssessment(result));

            // Only show extra explanations when the
            // measurement itself was judged unreliable.
            if (result.Assessment.StartsWith(
                "Inconclusive",
                StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextWrapped(
                    CompactSignalReason(result));
            }

            if (result.Retries > 0)
            {
                ImGui.TextDisabled(
                    $"Retried {result.Retries} noisy trial(s).");
            }
        }

        // Copy only candidates with positive evidence.
        // This is distinct from the toolbar's UV-only copy.
        var likely = results
            .Where(r =>
                r.Assessment.StartsWith(
                    "Strong",
                    StringComparison.OrdinalIgnoreCase) ||
                r.Assessment.StartsWith(
                    "Possible",
                    StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Row)
            .ToArray();

        if (likely.Length > 0)
        {
            if (ImGui.SmallButton(
                "Copy likely rows##ADRICompactCopyLikely"))
            {
                ImGui.SetClipboardText(
                    string.Join(", ", likely));
            }
        }

        ImGui.TextDisabled(
            "Bars scaled to strongest signal; not dye coverage.");

        ImGui.TextDisabled(
            "Low or inconclusive does not mean unused.");
    }

    private void AppendAutoRowScanDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine(
            "[DEV7 STAGE 7 AUTOMATIC CANDIDATE SCAN]");

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
                    $"control {row.ControlMedian:F3}%; " +
                    $"net {row.NetSignal:F3}%; " +
                    $"retries {row.Retries}; " +
                    $"unstable trials {row.UnstableTrials}; " +
                    $"baseline strong {row.BaselineStrongPercent:F3}%; " +
                    row.Assessment);
            }
        }

        sb.AppendLine();
    }
}