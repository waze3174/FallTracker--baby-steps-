using System;
using System.Collections.Generic;
using System.Reflection;
using BabyStepsMenuLib;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FallTracker;

// waze3174's mods (FallTracker, Rulebook) each build their own pause menu page. with more than one installed,
// their pause buttons are hidden and a single MODS button opens a page listing them, so the pause menu doesn't
// run off the screen. the same code ships in each mod; they meet through the AppDomain (the menu library types
// are shared)
static class MenuHub
{
    const string ModsKey = "waze3174.menuhub.mods", MenusKey = "waze3174.menuhub.menus", ChooserKey = "waze3174.menuhub.chooser",
        FrameKey = "waze3174.menuhub.frame";

    // while starting (OnInitializeMelon): say this mod has a page, so everyone knows how many there will be
    internal static void Announce(string label)
    {
        var dom = AppDomain.CurrentDomain;
        if (dom.GetData(ModsKey) is not List<string> mods) { mods = new List<string>(); dom.SetData(ModsKey, mods); }
        mods.Add(label);
    }

    // once every mod has started (OnLateInitializeMelon): this mod's own page, and the MODS page if it's the first
    internal static void Build(string label, List<(string tab, Action<MenuInjectionLibrary.TabBuilder> build)> tabs, MelonLoader.MelonLogger.Instance log, HarmonyLib.Harmony harmony)
    {
        if (MenuInjectionLibrary.Logger == null) MenuInjectionLibrary.Logger = log;
        HookLibrary(harmony);

        var b = MenuInjectionLibrary.CreateMenu(label);
        foreach (var (tab, build) in tabs) b = b.AddTab(tab, build);
        var menu = b.AddFixedButton("Close").Build();

        var dom = AppDomain.CurrentDomain;
        if (dom.GetData(MenusKey) is not List<object[]> menus) { menus = new List<object[]>(); dom.SetData(MenusKey, menus); }
        menus.Add(new object[] { label, menu });

        int count = dom.GetData(ModsKey) is List<string> mods ? mods.Count : 1;
        if (count > 1 && dom.GetData(ChooserKey) == null)
        {
            // the list is read when the page is first shown, by then every mod has added its page
            var chooser = MenuInjectionLibrary.CreateMenu("Mods")
                .AddTab("Mods", ChooserPage)
                .AddFixedButton("Close")
                .Build();
            dom.SetData(ChooserKey, chooser);
            log.Msg($"{count} of waze3174's mods: one MODS button for their pages");
        }
    }

    // the MODS page's buttons, styled and highlighted by the mod that built it
    internal static void StyleWith(Action<Button> style) => buttonStyle = style;
    static Action<Button> buttonStyle;
    internal static readonly List<Button> ChooserButtons = new();

    static void ChooserPage(MenuInjectionLibrary.TabBuilder tab)
    {
        if (AppDomain.CurrentDomain.GetData(MenusKey) is not List<object[]> menus) return;
        foreach (var m in menus)
        {
            var target = (MenuInjectionLibrary.InjectedMenu)m[1];
            var btn = tab.AddButton((string)m[0], (UnityAction)(() => Open(target)));
            if (btn == null) continue;
            buttonStyle?.Invoke(btn);
            // as big as the game's own menu entries (follows its text size, so any resolution)
            var t = btn.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>(true);
            var src = Il2Cpp.Menu.me != null ? Il2Cpp.Menu.me.ContinueGameButtonText : null;
            if (t != null && src != null) t.fontSize = src.fontSize * 0.8f;
            ChooserButtons.Add(btn);
        }
    }

    // close MODS, open the mod's page the way its own (hidden) pause button would
    static void Open(MenuInjectionLibrary.InjectedMenu target)
    {
        if (AppDomain.CurrentDomain.GetData(ChooserKey) is MenuInjectionLibrary.InjectedMenu chooser)
            chooser.GetFixedButton(0)?.onClick.Invoke();
        var main = MainButton(target);
        if (main != null) main.GetComponent<Button>()?.onClick.Invoke();
        // the game's selection stays on the (hidden) pause button behind; next frame, select the page's first entry
        pendingSelect = target; pendingFrames = 2;
    }

    static MenuInjectionLibrary.InjectedMenu pendingSelect;
    static int pendingFrames;
    static FieldInfo listField;

    static void SelectFirst(MenuInjectionLibrary.InjectedMenu m)
    {
        listField ??= typeof(MenuInjectionLibrary.InjectedMenu).GetField("_submenuItemList", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (listField?.GetValue(m) is not Il2Cpp.MenuItemList list || list.items == null || list.items.Length == 0) return;
        var es = UnityEngine.EventSystems.EventSystem.current;
        var first = list.items[0];
        if (es == null || first == null) return;
        Il2Cpp.MenuItemList.active = list;
        list.selected = 0;
        es.SetSelectedGameObject(first);
    }

    // closing one of our pages leaves the cursor on CONTINUE (its own pause button is hidden). instead: closing a
    // mod's page reopens MODS on that mod's entry; closing MODS (or a mod's page when it's alone) puts the cursor
    // on the button that opened it. unpausing from a page just resumes the game
    const string LastOpenKey = "waze3174.menuhub.lastopen", ReturnKey = "waze3174.menuhub.return";
    static MethodInfo visibleMethod;

    static void BackToButton()
    {
        var dom = AppDomain.CurrentDomain;
        if (dom.GetData(MenusKey) is not List<object[]> menus) return;
        visibleMethod ??= typeof(MenuInjectionLibrary.InjectedMenu).GetMethod("GetSubmenuVisible", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (visibleMethod == null) return;
        var chooser = dom.GetData(ChooserKey) as MenuInjectionLibrary.InjectedMenu;

        object open = null;
        if (chooser != null && (bool)visibleMethod.Invoke(chooser, null)) open = chooser;
        foreach (var m in menus) if (open == null && (bool)visibleMethod.Invoke(m[1], null)) open = m[1];

        var last = dom.GetData(LastOpenKey);
        dom.SetData(LastOpenKey, open);
        var game = Il2Cpp.Menu.me;
        bool paused = game != null && game.paused && (int)game.currentMenuScreen == 0;
        if (open == null && last != null && paused && !(last == chooser && pendingSelect != null))
        {
            if (chooser != null && last != chooser)
            {
                // back to MODS, on the entry of the page we just left
                MainButton(chooser)?.GetComponent<Button>()?.onClick.Invoke();
                string label = null;
                foreach (var m in menus) if (m[1] == last) label = (string)m[0];
                dom.SetData(ReturnKey, new object[] { chooser, label, 3 });
            }
            else dom.SetData(ReturnKey, new object[] { chooser != null ? chooser : last, null, 3 });
        }

        // a few frames in a row: the library and the game move the cursor too
        if (dom.GetData(ReturnKey) is not object[] ret) return;
        int left = (int)ret[2] - 1;
        dom.SetData(ReturnKey, left > 0 ? new object[] { ret[0], ret[1], left } : null);
        var into = (MenuInjectionLibrary.InjectedMenu)ret[0];
        if (ret[1] is string want) SelectEntry(into, want);
        else if (open == null) SelectMain(into);
    }

    // the pause button that opens a page
    static void SelectMain(MenuInjectionLibrary.InjectedMenu m)
    {
        var target = MainButton(m);
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (target == null || !target.activeInHierarchy || es == null) return;
        es.SetSelectedGameObject(target);
        var list = target.transform.parent != null ? target.transform.parent.GetComponent<Il2Cpp.MenuItemList>() : null;
        if (list != null && list.items != null)
            for (int i = 0; i < list.items.Length; i++) if (list.items[i] == target) { list.selected = i; break; }
    }

    // the entry on a page whose text is `label`
    static void SelectEntry(MenuInjectionLibrary.InjectedMenu m, string label)
    {
        listField ??= typeof(MenuInjectionLibrary.InjectedMenu).GetField("_submenuItemList", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (listField?.GetValue(m) is not Il2Cpp.MenuItemList list || list.items == null) return;
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return;
        for (int i = 0; i < list.items.Length; i++)
        {
            var it = list.items[i];
            var t = it != null ? it.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>(true) : null;
            if (t == null || !string.Equals(t.text, label, StringComparison.OrdinalIgnoreCase)) continue;
            Il2Cpp.MenuItemList.active = list;
            list.selected = i;
            es.SetSelectedGameObject(it);
            return;
        }
    }

    static FieldInfo mainButtonField;
    static GameObject MainButton(MenuInjectionLibrary.InjectedMenu m)
    {
        mainButtonField ??= typeof(MenuInjectionLibrary.InjectedMenu).GetField("_mainButtonObj", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return mainButtonField?.GetValue(m) as GameObject;
    }

    // every frame after the library has shown the buttons (OnLateUpdate): hide the mods' own pause buttons and keep
    // them out of the gamepad list while the MODS button stands in for them. one mod does it each frame
    internal static void LateUpdate()
    {
        try { MenuLibFix.Apply(); } catch { }
        try { BackToButton(); } catch { }
        // a page opened from MODS by this copy of the code (each mod has its own copy, so before the frame check)
        if (pendingSelect != null && --pendingFrames <= 0) { SelectFirst(pendingSelect); pendingSelect = null; }
        var dom = AppDomain.CurrentDomain;
        if (dom.GetData(ChooserKey) == null || dom.GetData(MenusKey) is not List<object[]> menus) return;
        if (dom.GetData(FrameKey) is int f && f == Time.frameCount) return;
        dom.SetData(FrameKey, Time.frameCount);

        foreach (var m in menus)
        {
            var go = MainButton((MenuInjectionLibrary.InjectedMenu)m[1]);
            if (go == null) continue;
            if (go.activeSelf) go.SetActive(false);
            var list = go.transform.parent != null ? go.transform.parent.GetComponent<Il2Cpp.MenuItemList>() : null;
            if (list == null || list.items == null) continue;
            bool listed = false;
            foreach (var it in list.items) if (it == go) { listed = true; break; }
            if (!listed) continue;
            var keep = new List<GameObject>();
            foreach (var it in list.items) if (it != go) keep.Add(it);
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>(keep.Count);
            for (int i = 0; i < keep.Count; i++) arr[i] = keep[i];
            list.items = arr;
        }
    }

    // the library needs its Menu hooks once; another mod (the multiplayer mod) may have added them already
    static void HookLibrary(HarmonyLib.Harmony harmony)
    {
        var update = AccessTools.Method(typeof(Il2Cpp.Menu), "Update");
        var info = HarmonyLib.Harmony.GetPatchInfo(update);
        var libAsm = typeof(MenuInjectionLibrary).Assembly;
        if (info != null)
            foreach (var p in info.Prefixes)
                if (p.PatchMethod.DeclaringType?.Assembly == libAsm) return;
        harmony.PatchAll(libAsm);
    }
}
