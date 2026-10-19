using System.Collections.Generic;
using System.Linq;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Factions;

public class Page_ChooseIdeo_Multifaction : Page
{
    private enum Choice { Preset, CustomFixed, CustomFluid }

    public override string PageTitle => "ChooseYourIdeoligion".Translate();

    public Page_ChooseIdeoPreset pageChooseIdeo = new();
    private Choice choice;

    // Filled in by Page_ConfigureIdeo_Multifaction when the player finishes a custom ideoligion: either the
    // ideoligion itself (IdeoTransfer.Write) or the id of an existing one they picked from the list
    public string customIdeoXml;
    public int existingIdeoId = -1;

    public override void DoWindowContents(Rect inRect)
    {
        DrawPageTitle(inRect);
        float totalHeight = 0f;
        Rect mainRect = GetMainRect(inRect);
        TaggedString descText = "ChooseYourIdeoligionDesc".Translate();
        float descHeight = Text.CalcHeight(descText, mainRect.width);
        Rect descRect = mainRect;
        descRect.yMin += totalHeight;
        Widgets.Label(descRect, descText);
        totalHeight += descHeight + 10f;

        pageChooseIdeo.DrawStructureAndStyleSelection(inRect);

        Rect outRect = mainRect;
        outRect.width = 954f;
        outRect.yMin += totalHeight;
        float num3 = (InitialSize.x - 937f) / 2f;
        float tileX = (inRect.width - Page_ChooseIdeoPreset.ButtonSize.x - 10f - 500f - 16f) / 2f - num3;

        Widgets.BeginScrollView(
            viewRect: new Rect(0f - num3, 0f, 921f, pageChooseIdeo.totalCategoryListHeight + 100f),
            outRect: outRect,
            scrollPosition: ref pageChooseIdeo.leftScrollPosition);

        totalHeight = 0f;
        pageChooseIdeo.lastCategoryGroupLabel = "";

        // Clicking a preset selects it in the wrapped page; that unselects the custom tiles
        if (pageChooseIdeo.selectedIdeo != null)
            choice = Choice.Preset;
        DrawCustomTiles(tileX, ref totalHeight);

        foreach (IdeoPresetCategoryDef item in DefDatabase<IdeoPresetCategoryDef>.AllDefsListForReading.Where(c => c != IdeoPresetCategoryDefOf.Classic && c != IdeoPresetCategoryDefOf.Custom && c != IdeoPresetCategoryDefOf.Fluid))
        {
            pageChooseIdeo.DrawCategory(item, ref totalHeight);
        }
        pageChooseIdeo.totalCategoryListHeight = totalHeight;
        Widgets.EndScrollView();

        DoBottomButtons(inRect);
    }

    // The Fluid and Custom tiles as Page_ChooseIdeoPreset.DoWindowContents draws them. Classic is left out
    // (classic mode is a property of the whole game) and so is Load Saved (the editor has its own Load button).
    private void DrawCustomTiles(float x, ref float curY)
    {
        Widgets.Label(new Rect(0f, curY, 300f, Text.LineHeight), "CustomIdeoligions".Translate());
        GUI.color = new Color(1f, 1f, 1f, 0.5f);
        Widgets.DrawLineHorizontal(0f, curY + Text.LineHeight + 2f, 901f);
        GUI.color = Color.white;
        curY += 12f;

        Vector2 size = Page_ChooseIdeoPreset.ButtonSize;
        Rect fluidRect = new Rect(x, curY + Text.LineHeight, size.x, size.y);
        DrawTile(fluidRect, IdeoPresetCategoryDefOf.Fluid, "CreateCustomFluid".Translate(), Choice.CustomFluid);
        curY = fluidRect.yMax + 10f;

        Rect fixedRect = new Rect(x, curY + Text.LineHeight, size.x, size.y);
        DrawTile(fixedRect, IdeoPresetCategoryDefOf.Custom, "CreateCustomFixed".Translate(), Choice.CustomFixed);
        curY = fixedRect.yMax + 10f;
    }

    private void DrawTile(Rect rect, IdeoPresetCategoryDef category, string label, Choice tileChoice)
    {
        Rect descRect = new Rect(rect.xMax + 10f, rect.y, 500f, rect.height);
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(descRect, category.description);
        Text.Anchor = TextAnchor.UpperLeft;

        pageChooseIdeo.DrawSelectable(rect, label, null, TextAnchor.MiddleCenter, choice == tileChoice, tutorAllows: true, null, () =>
        {
            pageChooseIdeo.selectedIdeo = null;
            choice = tileChoice;
        });
    }

    public override bool CanDoNext()
    {
        if (choice == Choice.Preset && pageChooseIdeo.selectedIdeo == null)
        {
            Messages.Message("Please select a preset.", MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        return base.CanDoNext();
    }

    public override void DoNext()
    {
        if (choice == Choice.Preset)
        {
            customIdeoXml = null;
            existingIdeoId = -1;
            next.prev = this;
            base.DoNext();
            return;
        }

        // Like Page_ChooseIdeoPreset.DoCustomize: the editor goes between this page and the next one
        var editor = new Page_ConfigureIdeo_Multifaction(this, choice == Choice.CustomFluid) { prev = this, next = next };
        next.prev = editor;
        Find.WindowStack.Add(editor);
        Close();
    }

    public IdeologyData GetIdeologyData()
    {
        if (choice != Choice.Preset)
            return new IdeologyData(CustomIdeoXml: customIdeoXml ?? "", ExistingIdeoId: existingIdeoId);

        return new IdeologyData(pageChooseIdeo.selectedIdeo, pageChooseIdeo.selectedStructure, pageChooseIdeo.selectedStyles);
    }
}

// CustomIdeoXml: an ideoligion made in the editor, serialized with IdeoTransfer ("" for none).
// ExistingIdeoId: an ideoligion already in the game the player picked from the editor's list (-1 for none).
public record IdeologyData(
    IdeoPresetDef SelectedIdeo = null,
    MemeDef SelectedStructure = null,
    List<StyleCategoryDef> SelectedStyles = null,
    string CustomIdeoXml = "",
    int ExistingIdeoId = -1
) : ISyncSimple;
