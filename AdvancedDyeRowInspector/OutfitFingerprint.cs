using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private const int OutfitCheckIntervalMs = 2000;
    private const int OutfitChangeSettleMs = 250;

    private nint scannedActorAddress;
    private string? scannedOutfitSignature;
    private string? pendingOutfitSignature;
    private bool dyeOpeningCheck;
    private DateTime nextOutfitCheckUtc;

    private static string? MakeOutfitSignature(
        IEnumerable<(string Game, string Actual)> resources)
    {
        var entries = new SortedSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (game, actual) in resources)
        {
            if (string.IsNullOrWhiteSpace(game) ||
                !IsOutfit(game))
                continue;

            string kind = Kind(game);

            if (kind != "Model" &&
                kind != "Material" &&
                kind != "Texture")
                continue;

            entries.Add(
                game.Replace('\\', '/').Trim().ToLowerInvariant() +
                "\t" +
                actual.Replace('\\', '/').Trim().ToLowerInvariant());
        }

        return entries.Count == 0
            ? null
            : string.Join("\n", entries);
    }

    private string? ReadLiveOutfitSignature()
    {
        var actor = Objects.LocalPlayer;

        if (actor == null || !pathsIpc.HasFunction)
            return null;

        try
        {
            var result = pathsIpc.InvokeFunc(
                new ushort[] { (ushort)actor.ObjectIndex });

            if (result == null ||
                result.Length == 0 ||
                result[0] == null)
                return null;

            return MakeOutfitSignature(
                result[0]!.SelectMany(
                    pair => pair.Value.Select(
                        game => (Game: game, Actual: pair.Key))));
        }
        catch (Exception ex)
        {
            Log.Debug(
                "ADRI outfit signature unavailable: " + ex.Message);
            return null;
        }
    }

    private void RememberScannedAppearance(nint actorAddress)
    {
        scannedActorAddress = actorAddress;

        scannedOutfitSignature = MakeOutfitSignature(
            flat.Select(entry => (
                Game: entry.GamePath,
                Actual: entry.ActualPath)));

        pendingOutfitSignature = null;

        nextOutfitCheckUtc = DateTime.UtcNow
            .AddMilliseconds(OutfitCheckIntervalMs);
    }
}
