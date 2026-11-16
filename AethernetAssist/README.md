# AethernetAssist v3.1.0

Dalamud API 15 / .NET 10

**Stable release: v3.1.0**

## Features

- City Aethernet navigation and attunement checks
- Residential Aethernet in all five housing districts
- Housing ward selection and supported gate navigation
- Local area map-click navigation
- Overworld Aetheryte routing
- Cross-district same-city Aethernet routing
- Cosmic Exploration planet and local Aethernet travel
- Light and Chaos same-data-center World Visit
- Automatic inheritance of the active Dalamud theme

## Map routing

AethernetAssist recognizes supported Aetheryte and
Aethernet destinations on the local area map.

For same-city routing it prefers a free subnet route:

1. Find loaded, unlocked shards in the destination's
   exact Aethernet network.
2. Search within 50 yalms of the player.
3. Evaluate the nearest eligible source first.
4. Validate the calculated vnavmesh walking path.
5. Accept only paths up to 70 yalms.
6. Try another eligible source when the previous
   candidate has no valid path.

If no suitable free route remains, AethernetAssist
uses the city's main Aetheryte as a paid fallback.
For subnet destinations it then completes the
remaining native Aethernet leg.

Paid routing respects AutoPaidMapRouting and
ConfirmMapRoutes. A paid teleport is never silently
accepted when the user's settings require confirmation.

The same-city free-route preference is intended for
the local area map. World-map route selection is
not part of this feature.

## Residential routing

- Main division and subdivision destinations
- HousingAethernet destination resolution
- Native menu identity and callback validation
- Arrival detection and guarded callback retries
- Residential map-click integration
- Existing manual destination-list travel

## Housing

- Ward selection 1-30
- Supported city-to-ward travel
- Supported housing entrances and gate movement
- Manual movement cancellation

## Cosmic Exploration

- Sinus Ardorum
- Phaenna
- Oizys
- Auxesia
- Local Cosmic Aethernet navigation

## Same-data-center World Visit

Available at eligible main-city Aetherytes in
Limsa Lominsa, New Gridania and Ul'dah.

The plugin validates native destination lists and
automatically confirms only the exact destination-
specific confirmation prompt.

Unknown prompts are never accepted automatically.
FFXIV controls accepted and queued transfers.

## Appearance

AethernetAssist inherits the active Dalamud/ImGui
colors and styling dynamically. There is no separate
manual theme selector.

## Commands

- `/anet` — Open navigation
- `/anet housing` — Open Housing
- `/anet settings` — Open Settings
- `/anet debug` — Toggle diagnostics
- `/anet cancel` — Stop supported automation
- `/anet sinus`
- `/anet phaenna`
- `/anet oizys`
- `/anet auxesia`

## Release status

AethernetAssist v3.1.0 is the stable release.
The testing branch is synchronized to the same
AethernetAssist source and package.

Historical development notes remain in
KNOWN_ISSUES.md and Git history.