using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using JetBrains.Annotations;
using Verse;

namespace Multiplayer.Client.Desyncs;

public class JittedMethod
{
    [CanBeNull] public MethodBase method;
    [CanBeNull] public MethodBase from;
    [CanBeNull] public string name; // .NET: the name from the runtime's JIT event, no MethodBase
    [CanBeNull] public int[] mapTicks;
    public int worldTicks;
    public int timer;
    public bool inInterface;

    public string TimeString()
    {
        return $"m:{mapTicks?.Join(delimiter: ",") ?? "[]"} w:{worldTicks} t:{timer}";
    }

    public override string ToString()
    {
        var what = name ?? $"{method?.DeclaringType}.{method?.Name}";
        var fromText = from != null ? $" from {from.DeclaringType}.{from.Name}" : "";
        return $"{what}{fromText} {TimeString()} i:{inInterface}";
    }
}

// Which methods were compiled when, for the desync report. On Mono a profiler callback; on .NET an EventListener on
// the runtime's JIT events (MethodLoadVerbose). With tiered compilation off a method is compiled on its first call,
// so the list says which code paths ran for the first time right before a desync. The .NET listener delivers events
// on its own thread, so the tick stamps are those of the moment the event arrived, a few frames late at worst.
public static class JittedMethods
{
    private static Queue<JittedMethod> methodQueue = new();
    private static IntPtr profiler;
    private static bool adding;
    private static JitEventListener listener;

    public static void Init()
    {
        if (!Native.IsMono)
        {
            listener ??= new JitEventListener();
            return;
        }

        profiler = Native.mono_profiler_create(IntPtr.Zero);
        Native.mono_profiler_set_jit_done_callback(profiler, (_, method, _) =>
        {
            if (adding) return;

            try
            {
                adding = true;

                if (UnityData.IsInMainThread && Multiplayer.settings != null && Native.mono_method_get_token(method) != 0)
                {
                    var methodBase = Native.GetMethodBaseFromRuntimePointer(method);
                    Add(new JittedMethod
                    {
                        method = methodBase,
                        from = new StackTrace().GetFrame(1)?.GetMethod(),
                        mapTicks = Multiplayer.game?.asyncTimeComps.Select(c => c.mapTicks).ToArray(),
                        worldTicks = Multiplayer.game?.asyncWorldTimeComp?.worldTicks ?? -1,
                        timer = TickPatch.Timer,
                        inInterface = Multiplayer.InInterface
                    });
                }
            }
            finally
            {
                adding = false;
            }
        });
    }

    private static void Add(JittedMethod jitted)
    {
        lock (methodQueue)
        {
            methodQueue.Enqueue(jitted);
            if (methodQueue.Count > Multiplayer.settings.jittedMethodsInDesync)
                methodQueue.Dequeue();
        }
    }

    public static void OnApplicationQuit()
    {
        if (!Native.IsMono)
        {
            listener?.Dispose();
            listener = null;
            return;
        }

        Native.mono_profiler_set_jit_done_callback(profiler, null);
    }

    public static string GetJittedMethodsString()
    {
        JittedMethod[] snapshot;
        lock (methodQueue)
            snapshot = methodQueue.ToArray();
        var jittedMethods = snapshot.Select((j, i) => (j, i)).ToArray();

        var builder = new StringBuilder();
        builder.Append("In simulation:"); // This will get a newline from the != comparison below
        var timeString = "";

        foreach (var j in jittedMethods.Where(j => !j.j.inInterface))
        {
            if (timeString != j.j.TimeString())
                builder.AppendLine();
            builder.AppendLine($"{j.i} {j.j}");
            timeString = j.j.TimeString();
        }

        builder.AppendLine();
        builder.AppendLine();

        timeString = "";

        builder.Append("In interface:");
        foreach (var j in jittedMethods.Where(j => j.j.inInterface))
        {
            if (timeString != j.j.TimeString())
                builder.AppendLine();
            builder.AppendLine($"{j.i} {j.j}");
            timeString = j.j.TimeString();
        }

        return builder.ToString();
    }

    private sealed class JitEventListener : EventListener
    {
        private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
        private const EventKeywords JitKeyword = (EventKeywords)0x10;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == RuntimeProvider)
                EnableEvents(source, EventLevel.Verbose, JitKeyword);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            // MethodLoadVerbose fires once the code is ready; MethodJittingStarted would list methods that then fail
            if (e.EventName == null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal))
                return;
            if (e.PayloadNames == null || e.Payload == null || Multiplayer.settings == null)
                return;

            int ns = e.PayloadNames.IndexOf("MethodNamespace");
            int name = e.PayloadNames.IndexOf("MethodName");
            if (ns < 0 || name < 0)
                return;

            int[] mapTicks = null;
            try
            {
                // Read from the listener thread while the main thread may be changing the list
                mapTicks = Multiplayer.game?.asyncTimeComps.Select(c => c.mapTicks).ToArray();
            }
            catch (InvalidOperationException)
            {
            }

            Add(new JittedMethod
            {
                name = $"{e.Payload[ns]}.{e.Payload[name]}",
                mapTicks = mapTicks,
                worldTicks = Multiplayer.game?.asyncWorldTimeComp?.worldTicks ?? -1,
                timer = TickPatch.Timer,
                inInterface = Multiplayer.InInterface
            });
        }
    }
}
