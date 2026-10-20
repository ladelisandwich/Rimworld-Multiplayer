using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Verse;

namespace Multiplayer.Client.Desyncs;

// Desync traces on .NET. The Mono walker (DeferredStackTracingImpl) reads return addresses off the stack for next
// to nothing; here a traced call records a System.Diagnostics.StackTrace, about 20 µs each on .NET 10 at the game's
// stack depth, so only the first MaxTracesPerTick calls of each map (and the world) per tick are traced, and only
// with the "slow" tracing setting on. Frames are hashed by declaring type and method name (GenText.StableStringHash),
// never by address, so the hashes compare between clients. Left out because they aren't the same on every client:
// frames of the runtime's own libraries (their IL differs per OS) and runtime-generated stubs (InvokeStub_*, which
// CoreCLR emits for a reflection target only after a few invocations of it; seen as a false "Trace hashes don't
// match" on Oct 19).
public static class ManagedStackTracing
{
    public const int MaxTracesPerTick = 200;
    private const int HashInfluence = DeferredStackTracingImpl.HashInfluence;

    public static int tracesThisTick; // for the current map or world context
    private static int lastTimer = -1;
    private static readonly Dictionary<int, int> tracesThisTickByContext = new();

    private sealed class FrameInfo
    {
        public string name;
        public int hash;
        public bool skip;
    }

    private static readonly Dictionary<MethodBase, FrameInfo> frameInfos = new(Util.MethodIdentityComparer.Instance);
    private static readonly Regex harmonySuffix = new(@"_Patch\d+$", RegexOptions.Compiled);

    public static bool Enabled => !Native.IsMono && Multiplayer.settings.desyncTracingMode == DesyncTracingMode.Slow;

    // Records the calling stack as a desync trace. False when this tick's cap is reached.
    public static bool Record(string moreInfo = null, bool ignoreCap = false)
    {
        int timer = TickPatch.Timer;
        if (timer != lastTimer)
        {
            lastTimer = timer;
            tracesThisTickByContext.Clear();
        }

        // One budget per map and one for the world, so a busy first map can't starve the others of traces
        int context = Multiplayer.MapContext?.uniqueID ?? -1;
        tracesThisTickByContext.TryGetValue(context, out tracesThisTick);
        if (!ignoreCap && tracesThisTick >= MaxTracesPerTick)
            return false;
        tracesThisTickByContext[context] = ++tracesThisTick;

        var frames = new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>();
        var item = new StackTraceLogItemManaged();
        int hash = 0;
        int depth = 0;

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null) continue;

            var info = InfoFor(method);
            if (info.skip) continue;

            item.frames[depth] = info.name;
            if (depth < HashInfluence)
                hash = Gen.HashCombineInt(hash, info.hash);

            if (++depth == StackTraceLogItemSim.MaxDepth)
                break;
        }

        Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(item, depth, hash, moreInfo);
        return true;
    }

    private static FrameInfo InfoFor(MethodBase method)
    {
        if (frameInfos.TryGetValue(method, out var info))
            return info;

        var type = method.DeclaringType;
        // Harmony's replacement methods have no declaring type; their name is the original's full name plus _PatchN
        string name = type == null ? method.Name : $"{type}.{method.Name}";
        name = harmonySuffix.Replace(name, "");

        string ns = type?.Namespace ?? "";
        bool skip = ns.StartsWith("Multiplayer.Client.Desyncs")
                    || ns.StartsWith("HarmonyLib")
                    || ns.StartsWith("MonoMod")
                    || ns == "System"
                    || ns.StartsWith("System.")
                    // Other dynamic methods are the runtime's and the engine's stubs (InvokeStub_*, InvokeMessageMethod_*)
                    || type == null && !harmonySuffix.IsMatch(method.Name);

        frameInfos[method] = info = new FrameInfo { name = name, hash = GenText.StableStringHash(name), skip = skip };
        return info;
    }
}
