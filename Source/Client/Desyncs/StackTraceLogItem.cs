using System;
using System.Collections.Generic;
using System.Text;
using Multiplayer.Client.Desyncs;
using Verse;

namespace Multiplayer.Client
{
    public abstract class StackTraceLogItem : IDisposable
    {
        public int tick;
        public int hash;

        public abstract string AdditionalInfo { get; }

        public abstract string StackTraceString { get; }

        public override string ToString()
        {
            return $"Tick:{tick} Hash:{hash} '{AdditionalInfo}'\n{StackTraceString}";
        }

        public virtual void Dispose()
        {
        }
    }

    public class StackTraceLogItemObj : StackTraceLogItem
    {
        public string info1;
        public string info2;

        public override string AdditionalInfo => $"{info1} {info2}";

        public override string StackTraceString => "";
    }

    // A trace taken in the simulation together with the state at that moment. The frames come from the
    // runtime-specific subclasses: return addresses on Mono, method names on .NET.
    public abstract class StackTraceLogItemSim : StackTraceLogItem
    {
        public const int MaxDepth = 32;
        public int depth;

        public int ticksGame;
        public ulong rngState;
        public ThingDef thingDef;
        public int thingId;
        public string factionName;
        public string moreInfo;

        public override string AdditionalInfo => $"{ticksGame} {thingDef}{thingId} {factionName} {depth} {rngState} {moreInfo}";

        public override void Dispose()
        {
            depth = 0;
            ticksGame = 0;
            rngState = 0;
            thingId = 0;
            thingDef = null;
            factionName = null;
            moreInfo = null;
        }
    }

    public class StackTraceLogItemRaw : StackTraceLogItemSim
    {
        public long[] raw = new long[MaxDepth];

        private static Dictionary<long, string> methodNameCache = new();

        public override string StackTraceString
        {
            get
            {
                var builder = new StringBuilder();
                for (int i = 0; i < depth; i++)
                {
                    var addr = raw[i];

                    if (!methodNameCache.TryGetValue(addr, out string method))
                        methodNameCache[addr] = method = Native.MethodNameFromAddr(raw[i], false);

                    builder.AppendLine(method != null ? SyncCoordinator.MethodNameWithIL(method) : "Null");
                }

                return builder.ToString();
            }
        }

        public static StackTraceLogItemRaw GetFromPool()
        {
            return SimplePool<StackTraceLogItemRaw>.Get();
        }

        public override void Dispose()
        {
            base.Dispose();
            SimplePool<StackTraceLogItemRaw>.Return(this);
        }
    }

    // Frames recorded by ManagedStackTracing on .NET, already resolved to names
    public class StackTraceLogItemManaged : StackTraceLogItemSim
    {
        public string[] frames = new string[MaxDepth];

        public override string StackTraceString
        {
            get
            {
                var builder = new StringBuilder();
                for (int i = 0; i < depth; i++)
                    builder.AppendLine("at " + frames[i]);
                return builder.ToString();
            }
        }
    }
}
