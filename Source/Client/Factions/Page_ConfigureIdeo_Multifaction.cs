using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Saving;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Factions;

// The game's ideoligion editor, used from a running multiplayer game to make the ideoligion of a new faction.
// The editor expects the ideoligion it edits to be in IdeoManager and to be the player faction's primary, which
// in a running game would change the simulation on this client only. So the draft is kept out of the game: it is
// never the primary of any faction and it's only in IdeoManager's list while the UI draws (DraftIdeoVisibleToUi),
// which is where the editor looks for it. The finished ideoligion travels in the faction creation command.
public class Page_ConfigureIdeo_Multifaction : Page_ConfigureIdeo
{
    public static Page_ConfigureIdeo_Multifaction Open { get; private set; }

    // Window.PostOpen without Page_ConfigureIdeo.PostOpen: the base adopts the selected ideoligion as the
    // player's and prunes IdeoManager of unused ideoligions
    private static readonly Action<Window> WindowPostOpen =
        AccessTools.MethodDelegate<Action<Window>>(AccessTools.Method(typeof(Window), nameof(Window.PostOpen)), null, false);

    private readonly Page_ChooseIdeo_Multifaction presetPage;
    private readonly bool fluid;

    public Ideo draft;

    public Page_ConfigureIdeo_Multifaction(Page_ChooseIdeo_Multifaction presetPage, bool fluid)
    {
        this.presetPage = presetPage;
        this.fluid = fluid;
    }

    public override void PostOpen()
    {
        WindowPostOpen(this);
        Open = this;

        if (draft == null)
        {
            draft = IdeoUtility.MakeEmptyIdeo();
            draft.Fluid = fluid;
        }

        ideo = draft;
        IdeoUIUtility.SetSelected(draft);

        if (!draft.memes.Any())
            Find.WindowStack.Add(new Dialog_ChooseMemes(draft, MemeCategory.Structure, initialSelection: true));

        TutorSystem.Notify_Event("PageStart-ConfigureIdeo");
    }

    public override void PostClose()
    {
        base.PostClose();
        if (Open == this)
            Open = null;
    }

    public override void DoIdeos(Rect rect)
    {
        // No Create button (it would make the player faction's ideoligion) and only the draft is editable
        IdeoUIUtility.DoIdeoListAndDetails(GetMainRect(rect), ref scrollPosition_ideoList, ref scrollViewHeight_ideoList,
            ref scrollPosition_ideoDetails, ref scrollViewHeight_ideoDetails,
            editMode: true, showCreateIdeoButton: false, onlyEditIdeo: draft, ideoLoadedFromFile: UseLoadedIdeo);
    }

    // The editor's Load button: a saved ideoligion replaces the draft
    private void UseLoadedIdeo(Ideo loaded)
    {
        if (fluid && (loaded.memes.Count(m => m.category == MemeCategory.Normal) > IdeoFoundation.MemeCountRangeFluidAbsolute.max
                      || loaded.memes.Any(m => !IdeoUtility.IsMemeAllowedForInitialFluidIdeo(m))))
        {
            Messages.Message("ConfirmChangeMemesForFluidIdeo".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        loaded.Fluid = fluid;
        draft = loaded;
        ideo = loaded;
        IdeoUIUtility.SetSelected(loaded);
    }

    public override void Notify_ClosedChooseMemesDialog()
    {
        // The player backed out of the first memes dialog: back to the presets
        if (ideo != null && !ideo.memes.Any(m => m.category == MemeCategory.Normal))
            DoBack();
    }

    public override bool CanDoNext()
    {
        // Page_ConfigureIdeo.CanDoNext's checks; its last step, making the ideoligion the player's, is left out
        if (TutorSystem.TutorialMode && !TutorSystem.AllowAction("GotoNextPage"))
            return false;

        if (ideo == null)
        {
            Messages.Message("MessageMustChooseIdeo".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        if (ideo.name.NullOrEmpty())
        {
            Messages.Message("MessageIdeoNameCantBeEmpty".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        Pair<Precept, Precept> pair = ideo.FirstIncompatiblePreceptPair();
        if (pair != default)
        {
            Messages.Message("MessageIdeoIncompatiblePrecepts".Translate(pair.First.Label.Named("PRECEPT1"), pair.Second.Label.Named("PRECEPT2")).CapitalizeFirst(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        var ritualMissingTarget = ideo.FirstRitualMissingTarget();
        if (ritualMissingTarget != null)
        {
            Messages.Message("MessageRitualMissingTarget".Translate(ritualMissingTarget.Item1.LabelCap.Named("PRECEPT")) + ": " + ritualMissingTarget.Item2.ToCommaList().CapitalizeFirst() + ".", MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        Precept_Building buildingMissingRitual = ideo.FirstConsumableBuildingMissingRitual();
        if (buildingMissingRitual != null)
        {
            Messages.Message("MessageBuildingMissingRitual".Translate(buildingMissingRitual.LabelCap.Named("PRECEPT")), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        if (ideo == draft)
        {
            presetPage.customIdeo = IdeoTransfer.Write(draft);
            presetPage.existingIdeoId = -1;
        }
        else
        {
            // An ideoligion already in the game, picked from the list (the game allows this at game start too)
            presetPage.customIdeo = null;
            presetPage.existingIdeoId = ideo.id;
        }

        return true;
    }

    public override void DoBack()
    {
        draft = null;
        ideo = null;
        IdeoUIUtility.UnselectCurrent();
        presetPage.customIdeo = null;
        presetPage.existingIdeoId = -1;
        base.DoBack();
    }
}

// Puts the draft into IdeoManager's list for the duration of the UI pass only, so the editor and its dialogs
// find it while the simulation (ticks, saves, sync) never sees it
[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI))]
static class DraftIdeoVisibleToUi
{
    static void Prefix(ref Ideo __state)
    {
        var draft = Page_ConfigureIdeo_Multifaction.Open?.draft;
        if (draft == null || Current.Game?.World == null)
            return;

        var ideos = Find.IdeoManager.IdeosListForReading;
        if (ideos.Contains(draft))
            return;

        ideos.Add(draft);
        __state = draft;
    }

    static void Finalizer(Ideo __state)
    {
        if (__state != null)
            Find.IdeoManager.IdeosListForReading.Remove(__state);
    }
}

// Carries an ideoligion in a sync command, in the game's "external ideo" format (the ideo screen's Save/Load):
// ids, relics, ritual obligations and role holders are left out and precepts get new ids on load.
// An ideoligion is ~35 KB of XML and synced strings are capped at 32767 chars (ByteReader), so the XML goes
// gzipped and base64-encoded, cut into pieces well under the cap.
public static class IdeoTransfer
{
    private const int PieceLength = 30000;

    public static List<string> Write(Ideo ideo)
    {
        GameDataSaveLoader.isSavingOrLoadingExternalIdeo = true;
        byte[] xml;
        try
        {
            xml = ScribeUtil.WriteExposable(ideo);
        }
        finally
        {
            GameDataSaveLoader.isSavingOrLoadingExternalIdeo = false;
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(xml, 0, xml.Length);

        string encoded = Convert.ToBase64String(compressed.ToArray());
        var pieces = new List<string>();
        for (int i = 0; i < encoded.Length; i += PieceLength)
            pieces.Add(encoded.Substring(i, Math.Min(PieceLength, encoded.Length - i)));

        return pieces;
    }

    // Call while executing a command so the new ids are the same on every client
    public static Ideo Read(List<string> pieces)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(string.Concat(pieces)));
        using var xml = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            gzip.CopyTo(xml);

        GameDataSaveLoader.isSavingOrLoadingExternalIdeo = true;
        try
        {
            Ideo ideo = ScribeUtil.ReadExposable<Ideo>(xml.ToArray());
            ideo.id = Find.UniqueIDsManager.GetNextIdeoID();
            return ideo;
        }
        finally
        {
            GameDataSaveLoader.isSavingOrLoadingExternalIdeo = false;
        }
    }
}
