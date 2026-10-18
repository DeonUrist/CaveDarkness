using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CaveDarkness;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;
using UnityEngine.Rendering;

// Runs production SaveLighting with actual Harmony patches and managed stand-ins for
// Unity native objects. The installed game's serializer IL is checked separately.
internal static class Tests
{
    private static int _checks;
    private static void Equal<T>(T expected, T actual, string message)
    {
        _checks++;
        if (!Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual);
    }

    private static void Main(string[] args)
    {
        CheckGameSerializers(args[0]);
        SaveLighting.Install(new Harmony("cavedarkness.save-lighting.regression"));
        var loader = new ES3Types.ES3Type_GameObject();
        foreach (bool skinned in new[] { false, true })
        {
            Renderer r = skinned ? (Renderer)new SkinnedMeshRenderer() : new MeshRenderer();
            var go = new GameObject(); go.Renderers.Add(r);
            foreach (var mode in new[] { LightProbeUsage.Off, LightProbeUsage.BlendProbes, LightProbeUsage.UseProxyVolume })
            {
                for (int cycle = 0; cycle < 10; cycle++)
                {
                    Dyn.Originals[r] = mode;
                    r.lightProbeUsage = LightProbeUsage.CustomProvided; r.Block = true;
                    var writer = new ES3Writer();
                    if (skinned) new ES3Types.ES3Type_SkinnedMeshRenderer().Write(r, writer);
                    else new ES3Types.ES3Type_MeshRenderer().Write(r, writer);
                    Equal(mode, writer.Saved, "save uses original mode");
                    Equal(LightProbeUsage.CustomProvided, r.lightProbeUsage, "save leaves live lighting untouched");
                    Dyn.Originals.Clear(); r.lightProbeUsage = mode; r.Block = false;
                    loader.Load(new ES3Reader { Saved = writer.Saved }, go);
                    Equal(mode, r.lightProbeUsage, "repeated save/load remains bright");
                }
                foreach (bool unrelatedBlock in new[] { false, true })
                {
                    r.lightProbeUsage = mode; r.Block = unrelatedBlock;
                    loader.Load(new ES3Reader { Saved = LightProbeUsage.CustomProvided }, go);
                    Equal(mode, r.lightProbeUsage, "old save restores prefab mode");
                    Equal(unrelatedBlock, r.Block, "repair preserves unrelated property block");
                }
            }

            r.lightProbeUsage = LightProbeUsage.CustomProvided; r.Block = true;
            loader.Load(new ES3Reader { Saved = LightProbeUsage.CustomProvided }, go);
            Equal(LightProbeUsage.CustomProvided, r.lightProbeUsage, "another custom provider survives load");
            Equal(LightProbeUsage.CustomProvided, SaveLighting.SavedUsage(r), "another provider survives save");

            r.Block = false;
            loader.Load(new ES3Reader { Saved = LightProbeUsage.CustomProvided }, go);
            Equal(LightProbeUsage.BlendProbes, r.lightProbeUsage, "already orphaned renderer recovers");

            Dyn.Originals[r] = LightProbeUsage.Off; r.Block = true;
            loader.Load(new ES3Reader { Saved = LightProbeUsage.Off }, go);
            Equal(LightProbeUsage.CustomProvided, r.lightProbeUsage, "second load preserves active cave lighting");
            Equal(LightProbeUsage.Off, SaveLighting.SavedUsage(r), "second load preserves original mode for saving");
            Dyn.Originals.Clear();

            var part = new Part { R = r, OrigUsage = LightProbeUsage.BlendProbes };
            var cave = new Cave(); cave.Parts.Add(part); Caves.All.Add(cave);
            Equal(LightProbeUsage.BlendProbes, SaveLighting.SavedUsage(r), "baked cave props save original mode");
            part.Ours = true;
            Equal(LightProbeUsage.CustomProvided, SaveLighting.SavedUsage(r), "generated bands excluded from originals");
            Caves.All.Clear();
        }
        var addedGo = new GameObject();
        loader.Load(new ES3Reader { Saved = LightProbeUsage.CustomProvided, AddRenderer = true }, addedGo);
        Equal(LightProbeUsage.BlendProbes, addedGo.Renderers[0].lightProbeUsage, "new component without prefab mode recovers");
        Console.WriteLine("PASS: " + _checks + " checks (actual game serializer IL + Harmony save/load regressions)");
    }

    private static void CheckGameSerializers(string assemblyPath)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            foreach (string name in new[] { "MeshRenderer", "SkinnedMeshRenderer" })
            {
                var t = assembly.MainModule.GetType("ES3Types.ES3Type_" + name);
                var write = t.Methods.Single(m => m.Name == "WriteComponent");
                Equal(1, write.Body.Instructions.Count(i => i.Operand is MethodReference method &&
                    method.Name == "get_lightProbeUsage" && method.DeclaringType.FullName == "UnityEngine.Renderer"), "installed getter contract");
                var read = t.Methods.Single(m => m.Name == "ReadComponent");
                Equal(1, read.Body.Instructions.Count(i => i.Operand is MethodReference method &&
                    method.Name == "set_lightProbeUsage"), "installed loader persists lighting mode");
            }
            var components = assembly.MainModule.GetType("ES3Types.ES3Type_GameObject").Methods.Single(m => m.Name == "ReadComponents");
            Equal(false, components.HasGenericParameters, "load hook is non-generic");
            Equal("UnityEngine.GameObject", components.Parameters[1].ParameterType.FullName, "load hook argument contract");
        }
    }
}

namespace UnityEngine.Rendering
{
    public enum LightProbeUsage { Off, BlendProbes, UseProxyVolume, CustomProvided = 4 }
}
namespace UnityEngine
{
    public class Renderer
    {
        public string name = "test item";
        public LightProbeUsage lightProbeUsage { get; set; }
        public bool Block;
        public bool HasPropertyBlock() => Block;
    }
    public class MeshRenderer : Renderer { }
    public class SkinnedMeshRenderer : Renderer { }
    public class GameObject
    {
        public readonly List<Renderer> Renderers = new List<Renderer>();
        public T[] GetComponents<T>() => Renderers.OfType<T>().ToArray();
    }
}
namespace CaveDarkness
{
    internal static class Plugin { internal static void Verbose(string message) { } }
    internal static class Dyn
    {
        internal static readonly Dictionary<Renderer, LightProbeUsage> Originals = new Dictionary<Renderer, LightProbeUsage>();
        internal static bool OriginalUsage(Renderer r, out LightProbeUsage usage) => Originals.TryGetValue(r, out usage);
    }
    internal class Part { public Renderer R; public bool Ours; public LightProbeUsage OrigUsage; }
    internal class Cave { public readonly List<Part> Parts = new List<Part>(); }
    internal static class Caves { internal static readonly List<Cave> All = new List<Cave>(); }
}
public class ES3Writer { public LightProbeUsage Saved; public void WriteProperty(string key, LightProbeUsage value) { Saved = value; } }
public class ES3Reader { public LightProbeUsage Saved; public bool AddRenderer; }
namespace ES3Types
{
    public class ES3Type_MeshRenderer
    {
        public void Write(Renderer r, ES3Writer writer) => WriteComponent(r, writer);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteComponent(object obj, ES3Writer writer) => writer.WriteProperty("lightProbeUsage", ((MeshRenderer)obj).lightProbeUsage);
    }
    public class ES3Type_SkinnedMeshRenderer
    {
        public void Write(Renderer r, ES3Writer writer) => WriteComponent(r, writer);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteComponent(object obj, ES3Writer writer) => writer.WriteProperty("lightProbeUsage", ((SkinnedMeshRenderer)obj).lightProbeUsage);
    }
    public class ES3Type_GameObject
    {
        public void Load(ES3Reader reader, GameObject go) => ReadComponents(reader, go);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadComponents(ES3Reader reader, GameObject go)
        {
            if (reader.AddRenderer) go.Renderers.Add(new MeshRenderer());
            foreach (var r in go.Renderers) r.lightProbeUsage = reader.Saved;
        }
    }
}
