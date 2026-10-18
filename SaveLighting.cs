using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace CaveDarkness
{
    // ES3 saves lightProbeUsage, but not the MaterialPropertyBlock containing our SH.
    // A saved CustomProvided renderer therefore reloads with no ambient light, and Dyn
    // deliberately skips it as someone else's custom lighting. Keep our override runtime-only.
    internal static class SaveLighting
    {
        private struct BeforeLighting
        {
            public LightProbeUsage Usage;
            public bool HadBlock;
        }

        internal static void Install(Harmony h)
        {
            foreach (string name in new[] { "MeshRenderer", "SkinnedMeshRenderer" })
            {
                var type = AccessTools.TypeByName("ES3Types.ES3Type_" + name);
                var write = type == null ? null : AccessTools.DeclaredMethod(type, "WriteComponent");
                if (write == null) throw new MissingMethodException("ES3Type_" + name, "WriteComponent");
                h.Patch(write, transpiler: new HarmonyMethod(typeof(SaveLighting), nameof(WriteLighting)));
            }

            // GameObject loads (including the game's second item-load pass) all use this
            // non-generic method. Snapshot the prefab's mode before ES3 overwrites it.
            var goType = AccessTools.TypeByName("ES3Types.ES3Type_GameObject");
            var read = goType == null ? null : AccessTools.DeclaredMethod(goType, "ReadComponents");
            if (read == null) throw new MissingMethodException("ES3Type_GameObject", "ReadComponents");
            h.Patch(read, prefix: new HarmonyMethod(typeof(SaveLighting), nameof(BeforeRead)),
                postfix: new HarmonyMethod(typeof(SaveLighting), nameof(AfterRead)));
        }

        private static IEnumerable<CodeInstruction> WriteLighting(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(Renderer), "lightProbeUsage");
            var replacement = AccessTools.Method(typeof(SaveLighting), nameof(SavedUsage));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("ES3 renderer lighting getter count: " + replaced);
        }

        internal static bool OriginalUsage(Renderer r, out LightProbeUsage usage)
        {
            if (Dyn.OriginalUsage(r, out usage)) return true;
            foreach (var cave in Caves.All)
                foreach (var part in cave.Parts)
                    if (!part.Ours && part.R == r) { usage = part.OrigUsage; return true; }
            usage = default(LightProbeUsage);
            return false;
        }

        internal static LightProbeUsage SavedUsage(Renderer r)
        {
            LightProbeUsage usage;
            return OriginalUsage(r, out usage) ? usage : r.lightProbeUsage;
        }

        private static void BeforeRead(GameObject __1, out Dictionary<Renderer, BeforeLighting> __state)
        {
            __state = new Dictionary<Renderer, BeforeLighting>();
            foreach (var r in __1.GetComponents<Renderer>())
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    __state[r] = new BeforeLighting { Usage = r.lightProbeUsage, HadBlock = r.HasPropertyBlock() };
        }

        private static void AfterRead(GameObject __1, Dictionary<Renderer, BeforeLighting> __state)
        {
            foreach (var r in __1.GetComponents<Renderer>())
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                LightProbeUsage original;
                if (OriginalUsage(r, out original))
                {
                    // A repeated load into a live renderer must retain its live SH too.
                    r.lightProbeUsage = LightProbeUsage.CustomProvided;
                    continue;
                }
                if (r.lightProbeUsage != LightProbeUsage.CustomProvided) continue;
                BeforeLighting before;
                bool existed = __state.TryGetValue(r, out before);
                // A colour/texture property block alone is not evidence of custom SH.
                // Preserve an existing custom provider, including its block.
                if (existed && before.Usage == LightProbeUsage.CustomProvided && before.HadBlock) continue;
                if (!existed && r.HasPropertyBlock()) continue;
                // Old saves have only the enum, no SH. Prefer the fresh prefab's mode
                // (including Off); newly added/already orphaned renderers use BlendProbes.
                original = existed && before.Usage != LightProbeUsage.CustomProvided ? before.Usage : LightProbeUsage.BlendProbes;
                r.lightProbeUsage = original;
                Plugin.Verbose("restored orphaned saved cave lighting: " + r.name);
            }
        }
    }
}
