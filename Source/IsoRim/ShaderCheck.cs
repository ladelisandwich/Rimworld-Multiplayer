using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Verse;
using GraphicsShaderJob = Waylith.Rendering.GraphicsShaderJob;
using RuntimeShaderCompiler = Waylith.Rendering.RuntimeShaderCompiler;
using RuntimeShaderLanguage = Waylith.Rendering.RuntimeShaderLanguage;
using RuntimeShaderStage = Waylith.Rendering.RuntimeShaderStage;
using ShaderModuleCache = Waylith.Rendering.ShaderModuleCache;
using ShaderPrewarmer = Waylith.Rendering.ShaderPrewarmer;
using ShaderSourceLoader = Waylith.Rendering.ShaderSourceLoader;
using ShaderVariantKey = Waylith.Rendering.ShaderVariantKey;
using ShaderWarmupPlan = Waylith.Rendering.ShaderWarmupPlan;

namespace IsoRim
{
    // Port-owned (outside the subtree, like MpBridge), a test switch: off unless the game is started with
    // -isorimshadercheck (scripts/check_isorim_shaders.py --headless starts it). Compiles every shader variant of
    // IsoRim's ShaderDefs with the game's own compiler (Waylith RuntimeShaderCompiler, Slang) from exactly the text
    // the game builds (ShaderWarmup.BuildPlan, ShaderModuleCache.BuildVariantSource: includes expanded, USE_TEXTURE /
    // INSTANCED / OUTLINE_MRT defines injected after #version), and reports each failure with Slang's message.
    // Made for a -Headless run: the headless game never compiles shaders by itself (its prewarm is off) and needs no
    // GPU for this. With -QuitAfterBoot the game quits on its own afterwards.
    //   -isorimshadercheck            IsoRim's ShaderDefs (defName IsoRim*)
    //   -isorimshadercheck=all        every registered ShaderDef (baseline: vanilla must come out clean)
    //   -isorimshadercheck=<folder>   IsoRim's ShaderDefs, GLSL taken from <folder> first (a Windows path under
    //                                 Proton, e.g. Z:\home\...\IsoRim\Shaders, no '='); the defs stay the installed ones
    // Results: "[IsoRim] shader check" lines in Player.log, and <save data>/IsoRimShaderCheck/report.txt with every
    // variant's exact source (.glsl, the line numbers Slang reports) and SPIR-V (.spv, for spirv-val).
    // Reads and compiles only: no game state, no Rand, nothing drawn.
    [StaticConstructorOnStartup]
    public static class ShaderCheck
    {
        private const string Switch = "isorimshadercheck";

        static ShaderCheck()
        {
            bool all = false;
            string folder = null;
            if (GenCommandLine.TryGetCommandLineArg(Switch, out string arg) && !arg.NullOrEmpty())
            {
                if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
                    all = true;
                else
                    folder = arg;
            }
            else if (!GenCommandLine.CommandLineArgPassed(Switch))
            {
                return;
            }
            try
            {
                Run(all, folder);
            }
            catch (Exception e)   // an engine type or member Run uses moved in a game update (Run is NoInlining)
            {
                Log.Error("[IsoRim] shader check could not run: " + e);
            }
        }

        private sealed class Result
        {
            public string Def, Name, Stage, Flags, Error;
            public bool CacheHit;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(bool all, string folder)
        {
            string outDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "IsoRimShaderCheck");
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            if (folder != null)
                UseShaderFolder(folder);

            // Engine-side layout checks (comment annotations, stage interface). Each logs its own
            // "ERROR [Vulkan] Shader '...'" line; a def with one is drawn magenta whatever the compiler says.
            var report = new StringBuilder();
            var defs = DefDatabase<ShaderDef>.AllDefsListForReading
                .Where(d => d.shaderProgram != null && (all || d.defName.StartsWith("IsoRim", StringComparison.Ordinal)))
                .ToList();
            int layoutErrors = 0;
            foreach (ShaderDef d in defs)
            {
                string vert = d.shaderProgram.vertexShaderFileName, frag = d.shaderProgram.fragmentShaderFileName;
                string why = null;
                try
                {
                    if (ShaderSourceLoader.CreateMaterialLayout(frag).HasErrors)
                        why = "material layout of " + frag;
                    else if (ShaderSourceLoader.CreateVertexInputLayout(vert).HasErrors)
                        why = "vertex inputs of " + vert;
                    else if (ShaderSourceLoader.HasStageInterfaceErrors(vert, frag))
                        why = vert + " -> " + frag + " interface";
                }
                catch (Exception e)
                {
                    why = e.Message;
                }
                if (why != null)
                {
                    layoutErrors++;
                    report.AppendLine("LAYOUT " + d.defName + ": " + why + " (see the ERROR [Vulkan] Shader lines in Player.log; the def draws magenta)");
                    Log.Error("[IsoRim] shader check: " + d.defName + ": " + why + " has errors (logged above as ERROR [Vulkan] Shader ...); every draw with it is magenta");
                }
            }

            HashSet<string> names = new HashSet<string>(defs.Select(d => d.defName));
            ShaderWarmupPlan plan = Verse.ShaderWarmup.BuildPlan();   // the game's prewarm set: every variant the draws can ask for
            List<GraphicsShaderJob> jobs = plan.Graphics.Where(j => names.Contains(j.Variant.Shader.Key)).ToList();

            MethodInfo build = typeof(ShaderModuleCache).GetMethod("BuildVariantSource", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(ShaderVariantKey), typeof(RuntimeShaderStage) }, null);
            if (build == null || build.ReturnType != typeof(ValueTuple<string, string>))
            {
                Log.Warning("[IsoRim] shader check: ShaderModuleCache.BuildVariantSource(ShaderVariantKey, RuntimeShaderStage) is gone; "
                    + "running the game's own prewarm instead, which logs only the first failure per file (WARN [Vulkan] ShaderPrewarm: failed to warm ...)");
                var fallback = new ShaderWarmupPlan();
                fallback.Graphics.AddRange(jobs);
                ShaderPrewarmer.Warm(fallback);
                return;
            }

            var results = new ConcurrentBag<Result>();
            // One at a time on this (the loading) thread: compiling from worker threads while the static constructors run
            // hung the game at load (Oct 26 test run).
            foreach (GraphicsShaderJob job in jobs)
            {
                var r = new Result { Def = job.Variant.Shader.Key, Stage = job.Stage.ToString(), Flags = Flags(job.Variant), Name = job.SourceFile };
                string source = null;
                try
                {
                    var built = ((string, string))build.Invoke(null, new object[] { job.Variant, job.Stage });
                    source = built.Item1;
                    r.Name = built.Item2;
                    byte[] spirv = RuntimeShaderCompiler.Compile(source, r.Name, "main", RuntimeShaderLanguage.Glsl, job.Stage, out bool cacheHit);
                    r.CacheHit = cacheHit;
                    File.WriteAllBytes(Path.Combine(outDir, r.Name + ".spv"), spirv);
                }
                catch (Exception e)
                {
                    r.Error = (e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e).Message;
                }
                if (source != null)
                    File.WriteAllText(Path.Combine(outDir, r.Name + ".glsl"), source);
                results.Add(r);
            }

            List<Result> sorted = results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            int failed = 0;
            foreach (Result r in sorted)
            {
                if (r.Error == null)
                {
                    report.AppendLine("OK     " + r.Name + "  (" + r.Def + ", " + r.Stage + r.Flags + (r.CacheHit ? ", from the shader cache" : "") + ")");
                    continue;
                }
                failed++;
                report.AppendLine("FAIL   " + r.Name + "  (" + r.Def + ", " + r.Stage + r.Flags + ")");
                report.AppendLine(r.Error);
                Log.Error("[IsoRim] shader check: " + r.Name + " (" + r.Def + ", " + r.Stage + r.Flags + ") failed: " + r.Error);
            }
            string summary = $"{sorted.Count} variants of {defs.Count} ShaderDefs compiled with the game's compiler, {failed} failed, "
                + $"{layoutErrors} defs with layout errors";
            report.Insert(0, summary + Environment.NewLine);
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
            Log.Message("[IsoRim] shader check: " + summary + ". Report: " + Path.Combine(outDir, "report.txt"));
        }

        private static string Flags(ShaderVariantKey v)
        {
            return (v.UseTexture ? ", USE_TEXTURE" : "") + (v.Instanced ? ", INSTANCED" : "") + (v.OutlineMrt ? ", OUTLINE_MRT" : "");
        }

        // The game's mod shader folders (ShaderDefRegistry.RegisterShaderDirectories: running mods, last first), with
        // <folder> in front, so the GLSL comes from there. Only for a test run: the game draws from them too afterwards.
        private static void UseShaderFolder(string folder)
        {
            var dirs = new List<string> { Path.GetFullPath(folder) };
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                foreach (string f in mods[i].foldersToLoadDescendingOrder)
                {
                    string s = Path.Combine(f, "Shaders");
                    if (Directory.Exists(s))
                        dirs.Add(s);
                }
            }
            ShaderSourceLoader.SetOverrideShaderDirectories(dirs);
            ShaderSourceLoader.ClearLayoutCaches();
            Log.Message("[IsoRim] shader check: GLSL from " + dirs[0] + " first");
        }
    }
}
