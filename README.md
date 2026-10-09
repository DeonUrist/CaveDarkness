# CaveDarkness

BepInEx 5 plugin for **Apocalypter**: caves are dark places instead of the whole world dimming when you walk into one.

## What it changes

Vanilla: passing a cave mouth (`Cave_IN` trigger) makes the Player's PlayMaker FSM `CaveDarkness` ease Azure's
`environmentIntensity` (= `RenderSettings.ambientIntensity`, global) to 0.15; `Cave_OUT` eases it back. Everything everywhere gets
darker, including the daylight world seen through the cave mouth.

CaveDarkness:

- skips that global change (Harmony prefix on `SetProperty` for the `CaveDarkness` FSM only; the FSM still runs, so `CaveDarkness_bool`
  keeps working for other mods such as Apocaplayer);
- darkens the cave itself. Once per cave (in the background, 1.5 ms per frame) it measures how much sky each part of the cave can see
  with raycasts and how far it is from the entrance (the `Cave_IN` trigger), and gives each renderer its own ambient light (`LightProbeUsage.CustomProvided` + the sky's ambient SH scaled down);
- splits the big rock shells (`Rock1..5_Cave`, `cave_entrance`, `camp_base`) into ~10 light bands, so the outside of the rock stays lit
  and the inside fades to dark from the mouth inward
  (light = depth darkness — MaxDarkness reached at DarknessGradient metres, quick fall-off — lifted back up where the sky is visible). The game's meshes aren't CPU-readable, so copies ship in `cavemeshes.bin`;
- cave floors are the terrain, which can't take a per-object ambient: inside each cave a thin copy of the terrain surface (0.5 m grid, the terrain's
  own material and splat map, 3 cm above it) is drawn in light bands instead; the real terrain stays underneath for collisions;
- adds a black reflection probe over the inside (no sky reflections on wet rock in the dark);
- holds the eye adaptation (post-processing AutoExposure, if a profile has it on) while you are in the dark part, so it doesn't brighten the cave back up;
- NPCs, items, cars and the player (body, arms, held gear) take the light of their spot while they are in a cave and fade back outside.

Point lights (lamps, fires, your flashlight) are untouched, so they now matter in caves.

## Install

Copy the `CaveDarkness` folder (CaveDarkness.dll + cavemeshes.bin) into `BepInEx\plugins\`.

## Config (`BepInEx\config\com.denis.apocalypter.cavedarkness.cfg`)

| Section | Key | Default | |
| --- | --- | --- | --- |
| General | Enabled | true | Off = vanilla behaviour (all changes are undone at once) |
| General | MaxDarkness | 0.97 | How dark the deepest part of a cave gets (1 = pitch black, 0 = as bright as outside) |
| General | DarknessGradient | 12 | Metres from the cave entrance to the full darkness (smaller = it gets dark sooner) |
| Debug | VerboseLog | false | Log every cave found |
| Debug | TintTest | false | Everything the mod darkens is lit flat red instead (bright red = full light, black = darkest); in a cave it also logs the render state and what is under the crosshair |

Each baked cave logs one line (`cave Cave_3(Clone): 2 rock mesh(es) -> ... light bands, ... other object(s) darkened, ... rays, ... ms`).

## Build

`./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars point at the game's `Managed` and `BepInEx\core` folders), or `dotnet build`
(deploys DLL + cavemeshes.bin to `BepInEx\plugins\CaveDarkness`). `tools/export_cavemeshes.py` regenerates `cavemeshes.bin` from the game's assets.
