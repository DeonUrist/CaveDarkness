# CaveDarkness

BepInEx 5 plugin for **Apocalypter**: caves are dark places instead of the whole world dimming when you walk into one.

In vanilla, walking through a cave mouth turns down the ambient light of the **entire world** (Azure's `environmentIntensity` → 0.15), so the
daylight outside the mouth goes grey too, and walking out brings it back. CaveDarkness leaves the world alone and makes the caves themselves dark:

- the deeper you walk in, the darker it gets (how dark and how fast are settings);
- the entrance, the outside of the rock and the ground under overhanging rock stay lit;
- the rock walls, the floor, props, NPCs, items, cars and your own character all follow the same darkness;
- lamps, fires and your flashlight light the cave as before, so they matter now; sunlight doesn't leak in through cracks.

## Install

Copy the `CaveDarkness` folder into `BepInEx\plugins\`. It holds three files, all needed:

| File | |
| --- | --- |
| `CaveDarkness.dll` | the plugin |
| `cavemeshes.bin` | readable copies of the cave rock meshes (the game's own can't be read at runtime) |
| `cavedarkness.bundle` | the terrain shader that darkens cave floors |

## Settings (`BepInEx\config\com.denis.apocalypter.cavedarkness.cfg`, also in the Apocasetter Mods menu)

| Section | Key | Default | |
| --- | --- | --- | --- |
| General | Enabled | true | Off = vanilla behaviour (everything is undone at once) |
| General | MaxDarkness | 0.97 | How dark the deepest part of a cave gets (1 = pitch black, 0 = as bright as outside) |
| General | DarknessGradient | 12 | Metres walked in from the cave mouth to the full darkness (smaller = it gets dark sooner) |
| Debug | VerboseLog | false | Log every cave found and baked, and in a cave the render state and what is under the crosshair |
| Debug | TintTest | false | With VerboseLog on: everything the mod darkens is lit flat red instead (bright red = full light, black = darkest) |

Changing MaxDarkness or DarknessGradient re-applies within about a second, also while you stand in a cave. Without VerboseLog the mod only
writes warnings (for example a missing file).

## How it works

- **Vanilla dimming off:** a Harmony prefix skips `SetProperty` in the Player's PlayMaker FSM `CaveDarkness` only; the FSM keeps running, so its
  `CaveDarkness_bool` (used by other mods such as Apocaplayer) still works.
- **One darkness field per cave**, built once in the background (1.5 ms per frame, ~1–2 s per cave): a voxel grid over the cave (0.5 m, coarser for
  very large ones) is split into rock and air — rock is every surface found by rays along X, Y and Z plus a layer under the ground, then grown by
  one voxel to seal cracks and the gap under the walls. Each air voxel gets its distance **through the air** to open sky (Dijkstra). The first
  2 m stay fully lit, then the light falls to MaxDarkness over DarknessGradient metres.
- **Rocks:** the big rock shells are subdivided and split into up to 16 light bands, each a renderer with its own ambient light
  (`LightProbeUsage.CustomProvided` + the sky's ambient SH scaled down). Dark bands are on layer 23, which the sun doesn't light. Props get one
  value each. A black reflection probe covers the dark inside.
- **Floor:** cave floors are the terrain. Terrains are drawn with `CaveDarkness/Terrain` (Unity's `Nature/Terrain/Standard` whose ambient and sun
  are multiplied by a 0.25 m light map per cave; point and spot lights untouched). Grass under the dark part is removed while the mod runs.
- **Moving things:** five times a second, NPCs, items, cars and the player (body, arms, held gear, Apocaplayer's character) inside a cave take the
  light of their spot and fade back outside.
- **Floating origin:** the game shifts all world coordinates every 1,000 m; the mod keeps its positions in MapMagic's frame, so nothing goes stale.
- **Save/load:** ES3 writes each renderer's original light-probe mode instead of the mod's temporary `CustomProvided` override. Older saves
  containing that override recover the prefab's lighting mode on load, so items can brighten normally outside caves. Existing custom lighting
  with a property block is preserved. This recovery also runs when cave darkness is disabled.

## Build

- Plugin: `./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars point at the game's `Managed` and `BepInEx\core` folders), or `dotnet build`
  (deploys the DLL + cavemeshes.bin to `BepInEx\plugins\CaveDarkness`).
- Shader bundle: open `Unity/` in Unity 2020.3.49f1, menu **CaveDarkness > Build shader bundle** (see `Unity/README.md`).
- `tools/export_cavemeshes.py` regenerates `cavemeshes.bin` from the game's assets.

For a build without deployment: `dotnet build -c Release -p:DeployToGame=false`.

## Save/load regression checks

Build `dotnet build tests/SaveLighting.Tests.csproj -c Release`, then run
`tests/bin/Release/net472/SaveLighting.Tests.exe "<game>/Apocalypter_Data/Managed/Assembly-CSharp-firstpass.dll"`.
These checks inspect the installed game's ES3 serializer IL and run the production save/load patches with Harmony against managed renderer
stand-ins. They cover ten consecutive cycles for mesh/skinned renderers, old-save recovery, original Off/BlendProbes/UseProxyVolume modes,
unrelated property blocks, existing custom lighting, repeated loads into tracked objects, cave props and newly added renderers.

In-game verification: save an item inside a dark cave, return to the menu and load, then carry it into daylight. Repeat several times, including
with held gear and a previously affected save. Check that it brightens outside and darkens again on re-entry; also check loading with the mod
disabled. The automated checks do not render a Unity scene.
