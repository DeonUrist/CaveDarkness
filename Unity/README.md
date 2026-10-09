# CaveDarkness shader project (Unity 2020.3.49f1)

The terrain can't take a per-object ambient like the cave rocks, so the cave floors need a shader: `CaveDarkness/Terrain` is Unity's
built-in `Nature/Terrain/Standard` (forward) whose ambient light (sky light + reflections) is multiplied by the plugin's cave maps.
Sun, flashlight, fires and lamps light the floor as before.

Build: open this folder as a project in Unity 2020.3.49f1, menu **CaveDarkness > Build shader bundle** (or batchmode
`Unity.exe -batchmode -quit -projectPath <this folder> -executeMethod CaveDarknessBuild.Build -logFile build.log`).
It writes `Bundle/cavedarkness.bundle` and copies it into `BepInEx\plugins\CaveDarkness\` when that folder exists.
