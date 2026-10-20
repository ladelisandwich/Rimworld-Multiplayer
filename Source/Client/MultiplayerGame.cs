using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LudeonTK;
using Multiplayer.API;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Client.Comp;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Factions;
using RimWorld;
using RimWorld.BaseGen;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public class MultiplayerGame
    {
        public SyncCoordinator sync = new();

        public AsyncWorldTimeComp asyncWorldTimeComp;
        public MultiplayerWorldComp worldComp;
        public MultiplayerGameComp gameComp;
        public List<MultiplayerMapComp> mapComps = new();
        public List<AsyncTimeComp> asyncTimeComps = new();
        public SharedCrossRefs sharedCrossRefs = new();
        public ThingsById thingsById = new();

        private Faction myFaction;
        public Faction myFactionLoading;

        public Dictionary<int, PlayerDebugState> playerDebugState = new();
        public DebugActionNode rootDebugActionNode;

        public Faction RealPlayerFaction => myFaction ?? myFactionLoading;

        // Invoking a type initializer through reflection re-ran it on Mono. On .NET it does nothing once the type
        // is initialized, so the defaults are captured at startup (InitMultiplayer) and restored from here.
        private static Dictionary<FieldInfo, object> debugSettingsDefaults;

        public static void CaptureDebugSettingsDefaults()
        {
            debugSettingsDefaults ??= typeof(DebugSettings)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsLiteral && !f.IsInitOnly)
                .ToDictionary(f => f, f => f.GetValue(null));
        }

        public MultiplayerGame()
        {
            DeferredStackTracing.acc = 0;
            // Oct 19 diagnostics for the SyncActions table losing its entries between startup and play
            Log.Message($"Multiplayer: SyncActions table holds {SyncActions.syncActions.Count} entries at game start");

            Toils_Ingest.cardinals = GenAdj.CardinalDirections.ToList();
            Toils_Ingest.diagonals = GenAdj.DiagonalDirections.ToList();
            GenAdj.adjRandomOrderList = null;
            CellFinder.mapEdgeCells = null;
            CellFinder.mapSingleEdgeCells = new List<IntVec3>[4];

            TradeSession.trader = null;
            TradeSession.playerNegotiator = null;
            TradeSession.deal = null;
            TradeSession.giftMode = false;

            DebugTools.curTool = null;
            ClearPortraits();
            RealTime.moteList.Clear();

            Room.nextRoomID = 1;
            District.nextDistrictID = 1;
            Region.nextId = 1;
            ListerHaulables.groupCycleIndex = 0;
            ListerHaulables.cellCycleIndices.Clear();

            ZoneColorUtility.nextGrowingZoneColorIndex = 0;
            ZoneColorUtility.nextStorageZoneColorIndex = 0;

            SetThingMakerSeed(1);

            CaptureDebugSettingsDefaults();
            foreach (var (field, value) in debugSettingsDefaults)
                field.SetValue(null, value);

            foreach (var resolver in DefDatabase<RuleDef>.AllDefs.SelectMany(r => r.resolvers))
                if (resolver is SymbolResolver_EdgeThing edgeThing)
                    edgeThing.randomRotations = new List<int>() { 0, 1, 2, 3 };

            // Same reason as DebugSettings above: reset the shuffled static array by hand
            SymbolResolver_SingleThing.tmpRotations = [Rot4.North, Rot4.South, Rot4.West, Rot4.East];

            FactionCreator.ClearData();
        }

        public static void ClearPortraits()
        {
            foreach (var (_, cachedPortraits) in PortraitsCache.cachedPortraits)
            {
                foreach (var portrait in cachedPortraits.ToList())
                {
                    var cached = portrait.Value;
                    cached.LastUseTime = Time.time - 2f; // RimWorld expires portraits that have been unused for more than 1 second
                    cachedPortraits[portrait.Key] = cached;
                }
            }

            PortraitsCache.RemoveExpiredCachedPortraits();
        }

        public void SetThingMakerSeed(int seed)
        {
            foreach (var maker in CaptureThingSetMakers.captured)
            {
                if (maker is ThingSetMaker_Nutrition n)
                    n.nextSeed = seed;
                if (maker is ThingSetMaker_MarketValue m)
                    m.nextSeed = seed;
            }
        }

        public void OnDestroy()
        {
            FactionContext.Clear();
            ThingContext.Clear();
        }

        public IEnumerable<Session> GetSessions(Map map)
        {
            return worldComp.sessionManager.AllSessions.ConcatIfNotNull(map?.MpComp().sessionManager.AllSessions);
        }

        public void ChangeRealPlayerFaction(int newFaction)
        {
            ChangeRealPlayerFaction(Find.FactionManager.GetById(newFaction));
        }

        public void ChangeRealPlayerFaction(Faction newFaction, bool regenMapDrawers = true)
        {
            myFaction = newFaction;
            FactionContext.Set(newFaction);
            worldComp.SetFaction(newFaction);

            foreach (Map m in Find.Maps)
                m.MpComp().SetFaction(newFaction);

            foreach (Map m in Find.Maps)
            {
                if (regenMapDrawers)
                    m.mapDrawer.RegenerateEverythingNow();

                foreach (var t in m.listerThings.AllThings)
                    if (t is ThingWithComps tc)
                        tc.GetComp<CompForbiddable>()?.UpdateOverlayHandle();
            }

            Find.MainTabsRoot?.EscapeCurrentTab();
            Find.ColonistBar?.MarkColonistsDirty();
        }
    }
}
