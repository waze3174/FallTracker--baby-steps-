using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BabyStepsMenuLib;
using HarmonyLib;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FallTracker;

// the FALL TRACKER page in the pause menu, built with BabyStepsMenuLib (optional: only touched when it's installed).
// Reports: read the rage reports one at a time, delete them. Options: stats on / off, delete every report
static class FallMenu
{
    static TMP_Text reportText;
    static Button prevBtn, nextBtn, deleteOne, deleteAll;
    static Toggle panelToggle;
    static int index;                 // 0 = newest
    static float confirmOneUntil, confirmAllUntil;
    const float ConfirmWindow = 3f;   // seconds the "press again" stays armed

    static bool built;   // our tabs are on the pause menu page (ours, or the shared one)

    internal const string Label = "Fall Tracker";
    internal static void Announce() => MenuHub.Announce(Label);

    internal static void Build(MelonLoader.MelonLogger.Instance log, HarmonyLib.Harmony harmony)
    {
        MenuHub.StyleWith(b => Style(b.GetComponentInChildren<TextMeshProUGUI>(true), 0.5f));
        MenuHub.Build(Label, new() { ("Reports", Reports), ("Options", Options) }, log, harmony);
    }

    static string[] ReportFiles()
    {
        try
        {
            if (!Directory.Exists(Core.ReportsDir)) return Array.Empty<string>();
            return Directory.GetFiles(Core.ReportsDir, "*.txt").OrderByDescending(f => f).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    static void Reports(MenuInjectionLibrary.TabBuilder tab)
    {
        built = true;
        // one label for the whole report: the library gives each row a fixed height, so keep it to three short lines
        reportText = tab.AddLabel("");
        Style(reportText, 0.3f);
        if (reportText != null) { reportText.enableWordWrapping = false; reportText.richText = true; }
        // newer / older, wrapping around
        Style((prevBtn = tab.AddButton("Previous report", (UnityAction)(() => { int n = ReportFiles().Length; index = n == 0 ? 0 : (index - 1 + n) % n; Refresh(); })))?.GetComponentInChildren<TextMeshProUGUI>(true), 0.5f);
        Style((nextBtn = tab.AddButton("Next report", (UnityAction)(() => { int n = ReportFiles().Length; index = n == 0 ? 0 : (index + 1) % n; Refresh(); })))?.GetComponentInChildren<TextMeshProUGUI>(true), 0.5f);
        deleteOne = tab.AddButton("Delete this report", (UnityAction)DeleteOne);
        Style(deleteOne?.GetComponentInChildren<TextMeshProUGUI>(true), 0.5f);
        Refresh();
    }

    static void Options(MenuInjectionLibrary.TabBuilder tab)
    {
        built = true;
        panelToggle = tab.AddToggle("Show stats on the pause menu", Core.ShowPanel.Value, (UnityAction<bool>)(v => { Core.ShowPanel.Value = v; MelonLoader.MelonPreferences.Save(); }));
        Style(panelToggle?.GetComponentInChildren<TextMeshProUGUI>(true), 0.5f);
        deleteAll = tab.AddButton("Delete all rage reports", (UnityAction)DeleteAll);
        Style(deleteAll?.GetComponentInChildren<TextMeshProUGUI>(true), 0.5f);
    }

    static void DeleteOne()
    {
        var files = ReportFiles();
        if (files.Length == 0) return;
        if (Time.unscaledTime > confirmOneUntil) { confirmOneUntil = Time.unscaledTime + ConfirmWindow; return; }   // first press arms it
        confirmOneUntil = 0f;
        index = Mathf.Clamp(index, 0, files.Length - 1);
        try { File.Delete(files[index]); Core.Inst.LoggerInstance.Msg("deleted rage report " + Path.GetFileName(files[index])); }
        catch (Exception e) { Core.Inst.LoggerInstance.Warning("couldn't delete the report: " + e.Message); }
        Refresh();
    }

    static void DeleteAll()
    {
        if (Time.unscaledTime > confirmAllUntil) { confirmAllUntil = Time.unscaledTime + ConfirmWindow; return; }
        confirmAllUntil = 0f;
        int n = 0;
        foreach (var f in ReportFiles()) try { File.Delete(f); n++; } catch { }
        Core.Inst.LoggerInstance.Msg($"deleted {n} rage reports");
        index = 0;
        Refresh();
    }

    static void Refresh()
    {
        var files = ReportFiles();
        if (files.Length == 0)
        {
            index = 0;
            if (reportText != null) reportText.text = "NO RAGE REPORTS YET\n<size=80%>one is written when you quit the game after falling at least once</size>";
            return;
        }
        index = Mathf.Clamp(index, 0, files.Length - 1);
        string body;
        try { body = Compact(File.ReadAllLines(files[index])); } catch (Exception e) { body = "couldn't read it: " + e.Message; }
        if (reportText != null) reportText.text = $"REPORT {index + 1} OF {files.Length}  <size=80%>(NEWEST FIRST)</size>  " + body;
    }

    // the report file, short: "rage report <date>" / "this session: ..." / "save slot N so far: ..." -> three short lines
    static string Compact(string[] lines)
    {
        string date = "", session = "", save = "";
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith("rage report ")) date = l.Substring("rage report ".Length);
            else if (l.StartsWith("this session: ")) session = Short(l.Substring("this session: ".Length));
            else if (l.StartsWith("save slot ")) { int c = l.IndexOf(':'); save = (c > 0 ? l.Substring(0, c).Replace("save slot", "SAVE").Replace(" so far", "") : "SAVE") + "  " + Short(c > 0 ? l.Substring(c + 1).Trim() : l); }
        }
        return $"<size=80%>{date.ToUpperInvariant()}</size>\nSESSION  {session.ToUpperInvariant()}\n{save.ToUpperInvariant()}";
    }

    // "4 falls, 2.3 m lost, biggest drop 1.0 m, 21s on the ground" -> "4 falls · 2.3 m lost · biggest 1.0 m · 21s down"
    static string Short(string s) => s.Replace(", biggest drop ", " · biggest ").Replace(" on the ground", " down").Replace(", ", " · ");

    // live bits: "press again" prompts, the selected row's highlight, the list when reports appear
    static int lastCount = -1;

    internal static void Update()
    {
        if (!built && MenuHub.ChooserButtons.Count == 0) return;
        var m = Il2Cpp.Menu.me;
        if (m == null || !m.paused) return;
        bool armOne = Time.unscaledTime < confirmOneUntil, armAll = Time.unscaledTime < confirmAllUntil;
        SetText(deleteOne, armOne ? "Press again to delete" : "Delete this report");
        SetText(deleteAll, armAll ? "Press again to delete all" : "Delete all rage reports");
        int count = ReportCountCheap();
        if (count != lastCount) { lastCount = count; Refresh(); }

        var es = UnityEngine.EventSystems.EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        Color normal = MenuColor(false), selected = MenuColor(true);
        Highlight(panelToggle, sel, normal, selected);
        Highlight(prevBtn, sel, normal, selected);
        Highlight(nextBtn, sel, normal, selected);
        Highlight(deleteOne, sel, normal, selected);
        Highlight(deleteAll, sel, normal, selected);
        foreach (var b in MenuHub.ChooserButtons) Highlight(b, sel, normal, selected);
    }

    // each control's text, looked up once
    static readonly Dictionary<int, TextMeshProUGUI> texts = new();

    static TextMeshProUGUI TextOf(Selectable s)
    {
        int id = s.GetInstanceID();
        if (!texts.TryGetValue(id, out var t) || t == null) texts[id] = t = s.GetComponentInChildren<TextMeshProUGUI>(true);
        return t;
    }

    static float nextCount; static int cachedCount;
    static int ReportCountCheap()
    {
        if (Time.unscaledTime < nextCount) return cachedCount;
        nextCount = Time.unscaledTime + 1f;
        cachedCount = ReportFiles().Length;
        return cachedCount;
    }

    static void SetText(Button b, string text)
    {
        var t = b != null ? TextOf(b) : null;
        if (t != null && t.text != text) t.text = text;
    }

    static readonly Dictionary<int, FontStyles> baseStyle = new();

    static void Highlight(Selectable s, GameObject sel, Color normal, Color selected)
    {
        if (s == null || !s.gameObject.activeInHierarchy) return;
        var t = TextOf(s);
        if (t == null) return;
        bool on = sel != null && sel == s.gameObject;
        t.color = on ? selected : normal;
        // keep the text's own style (the game's buttons are bold caps), only add the underline
        int id = t.GetInstanceID();
        if (!baseStyle.TryGetValue(id, out var st)) { st = t.fontStyle & ~FontStyles.Underline; baseStyle[id] = st; }
        t.fontStyle = on ? st | FontStyles.Underline : st;
    }

    // the game's menu text colour: its buttons' normal (pale) or selected tint
    static Color MenuColor(bool selected)
    {
        var m = Il2Cpp.Menu.me;
        TMP_Text src = m != null ? m.ContinueGameButtonText : null;
        var c = src != null ? src.color : Color.white;
        var btn = m != null ? m.ContinueGameButton : null;
        if (btn != null) { var cb = btn.colors; c *= (selected ? cb.selectedColor : cb.normalColor) * cb.colorMultiplier; c.a = Mathf.Min(c.a, 1f); }
        return c;
    }

    // the library makes text as big as the menu's buttons, in white: smaller, in the menu's colour
    static readonly HashSet<int> styled = new();

    static void Style(TMP_Text t, float scale)
    {
        if (t == null || !styled.Add(t.GetInstanceID())) return;
        var m = Il2Cpp.Menu.me;
        TMP_Text src = m != null ? m.ContinueGameButtonText : null;
        if (src != null) t.fontSize = src.fontSize * scale; else t.fontSize *= scale;
        t.enableAutoSizing = false;
        t.color = MenuColor(false);
    }
}
