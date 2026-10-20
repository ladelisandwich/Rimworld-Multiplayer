using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace Multiplayer.Client
{
    static class SyncActions
    {
        static SyncAction<FloatMenuOption, WorldObject, Caravan, object> SyncWorldObjCaravanMenus;
        static Type CaravanActionConfirmationType;

        public static void Init()
        {
            void Error(string error)
            {
                Multiplayer.loadingErrors = true;
                Log.Error(error);
            }

            // TODO: Use MpMethodUtil instead if we decide to make it work with generic types/methods (already in MP Compat, so use it). Or remove this TODO if we decide not to.
            CaravanActionConfirmationType = AccessTools.Inner(typeof(CaravanArrivalActionUtility), "<>c__DisplayClass0_1`1");

            if (CaravanActionConfirmationType == null) Error($"Could not find type: {nameof(CaravanArrivalActionUtility)}.<>c__DisplayClass0_1<T>");

            // Patch in a long event, otherwise it causes errors with mods that are loading resources
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                SyncWorldObjCaravanMenus = RegisterActions((WorldObject obj, Caravan c) => obj.GetFloatMenuOptions(c), ActionGetter, WorldObjectCaravanMenuWrapper);
                SyncWorldObjCaravanMenus.PatchAll(nameof(WorldObject.GetFloatMenuOptions));

                // Oct 19: the postfix missed Settlement/MapParent/WorldObject.GetFloatMenuOptions in game although all
                // three were patched; this says whether the table was ever right
                var probe = AccessTools.DeclaredMethod(typeof(Settlement), nameof(WorldObject.GetFloatMenuOptions));
                if (probe == null || !syncActions.ContainsKey(probe))
                    Log.Error($"Multiplayer: SyncActions table has {syncActions.Count} entries and none for Settlement.GetFloatMenuOptions right after patching");
                else
                    Log.Message($"Multiplayer: SyncActions patched {syncActions.Count} methods");
            });
        }

        private static ref Action ActionGetter(FloatMenuOption o) => ref o.action;

        private static Action WorldObjectCaravanMenuWrapper(FloatMenuOption instance, WorldObject a, Caravan b, object c, Action original, Action sync)
        {
            if (instance.action.Method.DeclaringType is not { IsGenericType: true })
                return null;

            if (instance.action.Method.DeclaringType.GetGenericTypeDefinition() != CaravanActionConfirmationType)
                return null;

            return () =>
            {
                var field = AccessTools.DeclaredField(original.Target.GetType(), "action");
                if (!Multiplayer.ExecutingCmds)
                {
                    // If not in a synced call then replace the method that will be
                    // called by confirmation dialog with our synced method call.
                    field.SetValue(original.Target, sync);
                    original();
                    return;
                }

                // If we're in a synced call, just call the action itself (after confirmation dialog)
                ((Action)field.GetValue(original.Target))();
            };
        }

        static SyncAction<T, A, B, object> RegisterActions<T, A, B>(Func<A, B, IEnumerable<T>> func, ActionGetter<T> actionGetter, ActionWrapper<T, A, B, object> actionWrapper = null)
        {
            return RegisterActions<T, A, B, object>((a, b, c) => func(a, b), actionGetter, actionWrapper);
        }

        static SyncAction<T, A, B, C> RegisterActions<T, A, B, C>(Func<A, B, C, IEnumerable<T>> func, ActionGetter<T> actionGetter, ActionWrapper<T, A, B, C> actionWrapper = null)
        {
            var sync = new SyncAction<T, A, B, C>(func, actionGetter, actionWrapper);
            Sync.handlers.Add(sync);

            return sync;
        }

        public static Dictionary<MethodBase, ISyncAction> syncActions = new(MethodIdentityComparer.Instance);
        public static bool wantOriginal;
        private static bool syncingActions; // Prevents from running on base methods
        private static HashSet<string> loggedMisses = new();

        public static void SyncAction_Prefix(ref bool __state)
        {
            __state = syncingActions;
            syncingActions = true;
        }

        public static void SyncAction1_Postfix(object __instance, object __0, ref object __result, MethodBase __originalMethod, bool __state)
        {
            SyncAction2_Postfix(__instance, __0, null, ref __result, __originalMethod, __state);
        }

        public static void SyncAction2_Postfix(object __instance, object __0, object __1, ref object __result, MethodBase __originalMethod, bool __state)
        {
            if (!__state)
            {
                syncingActions = false;
                if (Multiplayer.ShouldSync && !wantOriginal && !syncingActions)
                {
                    if (!syncActions.TryGetValue(__originalMethod, out var action))
                        action = FindBySignature(__originalMethod) ?? FallbackHandler(__instance, __0, __originalMethod);

                    if (action != null)
                        __result = action.DoSync(__instance, __0, __1);
                    else if (loggedMisses.Add(__originalMethod.FullDescription()))
                        // The menu still opens, unsynced, instead of the exception taking the whole OnGUI down
                        Log.Error($"Multiplayer: no SyncAction for {Describe(__originalMethod)}; its options aren't synced. " +
                                  $"Table: {syncActions.Count} entries, same name: {SameName(__originalMethod)}");
                }
            }
        }

        // Oct 19: in game the table lookup missed Settlement/MapParent/WorldObject.GetFloatMenuOptions (first a
        // KeyNotFoundException, then a miss with MethodIdentityComparer) although PatchAll had stored all three; a
        // headless start showed the table right after patching. Until the cause is known, a miss falls back to the
        // method's signature and remembers the match, and logs what differed so the next log can explain it.
        private static ISyncAction FindBySignature(MethodBase method)
        {
            var signature = method.FullDescription();
            foreach (var (key, value) in syncActions)
            {
                if (key.FullDescription() != signature) continue;
                if (loggedMisses.Add(signature))
                    Log.Warning($"Multiplayer: SyncAction for {Describe(method)} found by signature only; " +
                                $"the stored key is {Describe(key)}, table {syncActions.Count} entries");
                syncActions[method] = value;
                return value;
            }
            return null;
        }

        // Every entry of the table maps a WorldObject.GetFloatMenuOptions(Caravan) override to this one handler, so
        // when the table has lost it the handler itself still syncs the menu (unsynced options ran the caravan
        // arrival on the client alone and desynced it, Oct 19)
        private static ISyncAction FallbackHandler(object instance, object arg0, MethodBase method)
        {
            if (instance is not WorldObject || arg0 is not Caravan || SyncWorldObjCaravanMenus == null)
                return null;
            if (loggedMisses.Add(method.FullDescription()))
                Log.Warning($"Multiplayer: SyncAction table has no entry for {Describe(method)}, using the world object " +
                            $"caravan menu handler directly. Table: {syncActions.Count} entries, same name: {SameName(method)}");
            syncActions[method] = SyncWorldObjCaravanMenus;
            return SyncWorldObjCaravanMenus;
        }

        private static string Describe(MethodBase m) =>
            $"{m.FullDescription()} (reflected {m.ReflectedType}, {m.GetType().Name}, token {m.MetadataToken:X}, " +
            $"module {m.Module.ModuleVersionId}, assembly {m.Module.Assembly.GetName().Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m.Module.Assembly)})";

        private static string SameName(MethodBase m) =>
            string.Join("; ", syncActions.Keys.Where(k => k.Name == m.Name).Select(k =>
                $"{Describe(k)} equal {MethodIdentityComparer.Instance.Equals(k, m)}"));
    }
}
