using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client
{
    public static class ScribeUtil
    {
        private const string RootNode = "root";

        private static MemoryStream writingStream;

        public static SharedCrossRefs sharedCrossRefs => Multiplayer.game.sharedCrossRefs;
        public static LoadedObjectDirectory defaultCrossRefs;

        public static bool loading;

        //dbg
        public static bool removeMapRefs;
        private static List<ILoadReferenceable> removedMapRefs;

        public static void StartWriting(bool indent = false)
        {
            writingStream = new MemoryStream();

            Scribe.mode = LoadSaveMode.Saving;
            XmlWriterSettings xmlWriterSettings = new XmlWriterSettings()
            {
                Indent = indent,
                OmitXmlDeclaration = true
            };

            XmlWriter writer = XmlWriter.Create(writingStream, xmlWriterSettings);
            Scribe.saver.writer = writer;
            writer.WriteStartDocument();
        }

        public static byte[] FinishWriting()
        {
            Scribe.saver.FinalizeSaving();

            byte[] arr = writingStream.ToArray();
            writingStream.Close();
            writingStream = null;

            return arr;
        }

        public static void StartWritingToDoc()
        {
            Scribe.mode = LoadSaveMode.Saving;
            XmlWriter xmlWriter = new CustomXmlWriter();
            Scribe.saver.writer = xmlWriter;
            xmlWriter.WriteStartDocument();
        }

        public static XmlDocument FinishWritingToDoc()
        {
            var doc = ((CustomXmlWriter)Scribe.saver.writer).doc;
            Scribe.saver.FinalizeSaving();
            return doc;
        }

        public static void InitFromXmlDoc(XmlDocument doc)
        {
            ScribeMetaHeaderUtility.loadedGameVersion = VersionControl.CurrentVersionStringWithRev;

            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
        }

        public static void FinalizeLoading()
        {
            if (!loading)
            {
                Log.Error("Called FinalizeLoading() but we aren't loading");
                return;
            }

            ScribeLoader loader = Scribe.loader;

            try
            {
                Scribe.ExitNode();

                loader.curXmlParent = null;
                loader.curParent = null;
                loader.curPathRelToParent = null;
                ResolveAllCrossReferences(loader.crossRefs);
                loader.initer.DoAllPostLoadInits();
            }
            catch (Exception e)
            {
                Log.Error("Exception in FinalizeLoading(): " + e);
                // ForceStop clears the cross refs, which must not hit the shared directory
                RestoreDefaultCrossRefs();
                loader.ForceStop();
                throw;
            }
            finally
            {
                loading = false;
            }
        }

        // CrossRefHandler.ResolveAllCrossReferences, minus the final Clear() of the loaded object directory. The
        // directory is the shared one here, and vanilla Clear() would empty it. LoadedObjectsClearPatch used to
        // catch that, but LoadedObjectDirectory.Clear is tiny: with tiered compilation off, the JIT inlines it
        // into CrossRefHandler.Clear, which is compiled while mod settings load, before any patch exists. So the
        // patch never saw those calls on 1.7 and every synced exposable wiped the directory. This does the same
        // work without relying on either patch.
        private static void ResolveAllCrossReferences(CrossRefHandler crossRefs)
        {
            Scribe.mode = LoadSaveMode.ResolvingCrossRefs;

            foreach (var exposable in crossRefs.crossReferencingExposables)
                if (exposable is ILoadReferenceable reffable)
                    sharedCrossRefs.RegisterLoadedTemp(reffable);

            foreach (var exposable in crossRefs.crossReferencingExposables)
            {
                try
                {
                    Scribe.loader.curParent = exposable;
                    Scribe.loader.curPathRelToParent = null;
                    exposable.ExposeData();
                }
                catch (Exception e)
                {
                    Log.Error("Could not resolve cross refs: " + e);
                }
            }

            Scribe.loader.curParent = null;
            Scribe.loader.curPathRelToParent = null;
            Scribe.mode = LoadSaveMode.Inactive;

            // What CrossRefHandler.Clear(errorIfNotEmpty: true) does, apart from clearing the directory, plus the
            // id fix-up that CrossRefHandler_Clear_Patch did when that Clear was still being called from here
            crossRefs.loadIDs.ConfirmClear();
            crossRefs.crossReferencingExposables.Clear();
            sharedCrossRefs.ReassignNegativeIdsInTemp();
            RestoreDefaultCrossRefs();
        }

        // What LoadedObjectsClearPatch does when the Harmony patch gets to run
        private static void RestoreDefaultCrossRefs()
        {
            if (Scribe.loader.crossRefs.loadedObjectDirectory is not SharedCrossRefs shared) return;

            Scribe.loader.crossRefs.loadedObjectDirectory = defaultCrossRefs;
            shared.UnregisterAllTemp();
        }

        public static XmlDocument LoadDocument(byte[] data)
        {
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream);

            XmlDocument xmlDocument = new XmlDocument();
            xmlDocument.Load(reader);

            return xmlDocument;
        }

        public static byte[] XmlToByteArray(XmlNode node, string rootNode = null, bool indent = false)
        {
            XmlWriterSettings xmlWriterSettings = new XmlWriterSettings()
            {
                Indent = indent,
                OmitXmlDeclaration = true
            };

            using MemoryStream stream = new MemoryStream();
            using XmlWriter writer = XmlWriter.Create(stream, xmlWriterSettings);

            if (rootNode != null)
                writer.WriteStartElement(rootNode);

            node.WriteTo(writer);

            if (rootNode != null)
                writer.WriteEndElement();

            writer.Flush();
            return stream.ToArray();
        }

        public static void SupplyCrossRefs()
        {
            if (sharedCrossRefs == null) return;

            if (!loading)
            {
                Log.Warning("Tried to supply cross refs without calling ScribeUtil.StartLoading()");
                return;
            }

            defaultCrossRefs ??= Scribe.loader.crossRefs.loadedObjectDirectory;
            Scribe.loader.crossRefs.loadedObjectDirectory = sharedCrossRefs;

            if (removeMapRefs)
            {
                removedMapRefs = new List<ILoadReferenceable>();
                foreach (var map in Find.Maps)
                    removedMapRefs.AddRange(sharedCrossRefs.UnregisterAllFrom(map));
            }

            MpLog.Debug($"Cross ref supply: {sharedCrossRefs.allObjectsByLoadID.Count} {sharedCrossRefs.allObjectsByLoadID.LastOrDefault()} {Faction.OfPlayer}");
        }

        private static readonly HashSet<string> reportedMissingRefs = new();

        // On 1.7 synced jobs sometimes reference spawned things that aren't in the shared cross refs (seen with
        // Equip/Wear orders). Until the cause is known this reports each case once, with what ThingsById knows
        // about the thing, and registers the thing when it exists so the reference still resolves.
        private static void SupplyMissingThingRefs()
        {
            var directory = sharedCrossRefs.allObjectsByLoadID;

            // Objects deep-loaded by this very read get registered when the cross refs are resolved, so a
            // reference to one of them isn't missing
            HashSet<string> deepLoaded = null;
            foreach (var exposable in Scribe.loader.crossRefs.crossReferencingExposables)
                if (exposable is ILoadReferenceable reffable)
                    (deepLoaded ??= new HashSet<string>()).Add(reffable.GetUniqueLoadID());

            foreach (var record in Scribe.loader.crossRefs.loadIDs.idsRead.Values)
            {
                var id = record.targetLoadID;
                if (id.NullOrEmpty() || id == "null" || !typeof(Thing).IsAssignableFrom(record.targetType)) continue;
                if (directory.ContainsKey(id) || deepLoaded != null && deepLoaded.Contains(id)) continue;

                Multiplayer.ThingsById.TryGetValue(Thing.IDNumberFromThingID(id), out var thing);
                bool found = thing != null && thing.GetUniqueLoadID() == id;

                if (reportedMissingRefs.Add(id))
                {
                    var known = found
                        ? $"ThingsById has it: spawned={thing.Spawned}, map={thing.Map?.uniqueID.ToString() ?? "none"}, destroyed={thing.Destroyed}, holder={thing.ParentHolder.ToStringSafe()}"
                        : thing != null ? $"ThingsById has a different thing with that number: {thing}" : "ThingsById doesn't have it";
                    Log.Warning($"Multiplayer: {record.parent.ToStringSafe()} ({record.pathRelToParent}) references {id}, which isn't in the shared cross refs ({directory.Count} entries), while handling {SyncUtil.currentHandler?.ToString() ?? "no sync command"}. {known}");
                    LogSharedCrossRefsSummary();
                }

                if (found)
                    directory[id] = thing;
            }
        }

        public static void LogSharedCrossRefsSummary()
        {
            int total = 0, missing = 0;
            var examples = new List<string>();

            foreach (var map in Find.Maps)
                foreach (var thing in map.listerThings.AllThings)
                {
                    if (!thing.def.HasThingIDNumber) continue;
                    total++;
                    var id = thing.GetUniqueLoadID();
                    if (sharedCrossRefs.allObjectsByLoadID.ContainsKey(id)) continue;
                    missing++;
                    if (examples.Count < 5) examples.Add(id);
                }

            Log.Message($"Multiplayer: shared cross refs hold {sharedCrossRefs.allObjectsByLoadID.Count} entries, " +
                        $"{missing} of {total} spawned things missing" + (missing > 0 ? $" (e.g. {examples.Join(", ")})" : ""));
        }

        public static byte[] WriteExposable(IExposable element, string name = RootNode, bool indent = false, Action beforeElement = null)
        {
            StartWriting(indent);
            Scribe.EnterNode(RootNode);
            beforeElement?.Invoke();
            Scribe_Deep.Look(ref element, name);
            return FinishWriting();
        }

        public static T ReadExposable<T>(byte[] data, Action<T> beforeFinish = null) where T : IExposable
        {
            // The game only deep-loads things while ProgramState is MapInitializing and some ExposeData paths
            // count on it: in Playing, Need_Food.MaxLevel reads a stat, which walks the pawn's hediffs before
            // their pawn field and the needs tracker exist (Hediff_Addiction.Need), so a pawn with an addiction
            // lost its food need when loaded from a command. Load the way a save loads.
            var prevProgramState = Current.programStateInt;
            if (prevProgramState == ProgramState.Playing)
                Current.programStateInt = ProgramState.MapInitializing;

            try
            {
                loading = true;
                InitFromXmlDoc(LoadDocument(data));
                SupplyCrossRefs();

                T element = default;
                Scribe_Deep.Look(ref element, RootNode);

                beforeFinish?.Invoke(element);

                if (removeMapRefs)
                {
                    sharedCrossRefs.Reregister(removedMapRefs);
                    removedMapRefs = null;
                }

                SupplyMissingThingRefs();
                FinalizeLoading();

                // Default cross refs restored in LoadedObjectsClearPatch

                return element;
            }
            finally
            {
                Current.programStateInt = prevProgramState;
            }
        }
    }
}
