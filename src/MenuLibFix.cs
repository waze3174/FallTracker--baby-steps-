using System;
using System.Collections;
using System.Reflection;
using BabyStepsMenuLib;
using HarmonyLib;

namespace FallTracker;

// BabyStepsMenuLib: every mod's menu picks which list the gamepad steers, each frame; a menu whose page is
// closed points it back at the pause buttons, so with several mods the last one wins and the open page loses
// its gamepad, and the game's pause buttons come back under it. after the library, hand the gamepad to whichever
// page is really open (any mod's) and keep the pause buttons hidden. the same code ships in several mods; running
// it twice is harmless
static class MenuLibFix
{
    static FieldInfo menusField, listField, mainCgField;
    static MethodInfo visibleMethod;
    static PropertyInfo kbVisible;
    static bool broken;

    // the game moves the cursor inside Menu.Update using MenuItemList.active; the library sets that in its own
    // prefix, closed pages last-wins. so: after the library's prefix and before the game's code (prefix, lowest
    // priority), and again after everything (postfix) for the pause buttons it shows again
    [HarmonyPatch(typeof(Il2Cpp.Menu), "Update")]
    static class AroundMenuUpdate
    {
        [HarmonyPriority(Priority.Last)]
        static void Prefix() => Run();

        [HarmonyPriority(Priority.Last)]
        static void Postfix() => Run();

        static void Run()
        {
            if (broken) return;
            try { Fix(); }
            catch (Exception e) { broken = true; Core.Inst.LoggerInstance.Warning("menu library fix off: " + e.Message); }
        }
    }

    // the library's on-screen keyboard steers itself; with a page open the fix above (and its copies in other mods)
    // would hand the gamepad back to the page under it, and the keyboard's cursor jumps back to its first key every
    // frame. so: note what the library chose (right after its prefix), and while any keyboard shows, put that back
    // after every copy of the fix (lowest priority of all)
    static Il2Cpp.MenuItemList libChoice;

    [HarmonyPatch(typeof(Il2Cpp.Menu), "Update")]
    static class NoteLibChoice
    {
        [HarmonyPriority(Priority.Normal - 1)]
        static void Prefix() => libChoice = Il2Cpp.MenuItemList.active;
    }

    [HarmonyPatch(typeof(Il2Cpp.Menu), "Update")]
    static class KeyboardKeepsGamepad
    {
        [HarmonyPriority(Priority.Last - 1)]
        static void Prefix() { if (KeyboardOpen()) Il2Cpp.MenuItemList.active = libChoice; }
    }

    // the open page's library menu. a keyboard must be opened on it: the library then shrinks that page's list to
    // the selected key while it types (and puts it back after), so the game's own up / down can't take the cursor
    // off the keys. GetKeyboardMenu() is just the first menu any mod registered, usually not the open page's
    internal static Il2Cpp.MenuItemList ListOf(MenuInjectionLibrary.InjectedMenu m)
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        listField ??= typeof(MenuInjectionLibrary.InjectedMenu).GetField("_submenuItemList", any);
        return listField?.GetValue(m) as Il2Cpp.MenuItemList;
    }

    internal static MenuInjectionLibrary.InjectedMenu OpenPage()
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        menusField ??= typeof(MenuInjectionLibrary).GetField("_registeredMenus", any);
        visibleMethod ??= typeof(MenuInjectionLibrary.InjectedMenu).GetMethod("GetSubmenuVisible", any);
        if (menusField?.GetValue(null) is not IList menus || visibleMethod == null) return null;
        foreach (var m in menus)
            if (m is MenuInjectionLibrary.InjectedMenu im && (bool)visibleMethod.Invoke(im, null)) return im;
        return null;
    }

    static PropertyInfo anyKb;
    static bool noAnyKb;

    internal static bool KeyboardOpen()
    {
        if (noAnyKb) return false;
        try
        {
            if (anyKb == null)
            {
                const BindingFlags st = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                anyKb = typeof(MenuInjectionLibrary).GetProperty("IsAnyKeyboardVisible", st)
                    ?? typeof(MenuInjectionLibrary.InjectedMenu).GetProperty("IsAnyKeyboardVisible", st);
                if (anyKb == null) { noAnyKb = true; return false; }
            }
            return (bool)anyKb.GetValue(null);
        }
        catch { noAnyKb = true; return false; }
    }

    // the library rebuilds every page when F9 is pressed (a debug key of its own), and the rebuilt pages can come up
    // empty (only Close). F9 is also FlyAndQuicksave's (and PracticeTools') spot-save key, so while a library menu updates,
    // F9 reads as not pressed
    static int inLibUpdate;

    [HarmonyPatch(typeof(MenuInjectionLibrary.InjectedMenu), nameof(MenuInjectionLibrary.InjectedMenu.OnMenuUpdate))]
    static class NoF9Rebuild
    {
        static void Prefix() => inLibUpdate++;
        static void Finalizer() => inLibUpdate--;
    }

    [HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetKeyDown), typeof(UnityEngine.KeyCode))]
    static class F9Key
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(UnityEngine.KeyCode key, ref bool __result)
        {
            if (inLibUpdate == 0 || key != UnityEngine.KeyCode.F9) return true;
            __result = false;
            return false;
        }
    }

    static void Fix()
    {
        if (menusField == null)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            menusField = typeof(MenuInjectionLibrary).GetField("_registeredMenus", any);
            var t = typeof(MenuInjectionLibrary.InjectedMenu);
            listField = t.GetField("_submenuItemList", any);
            visibleMethod = t.GetMethod("GetSubmenuVisible", any);
            kbVisible = t.GetProperty("KbVisible", any);
            mainCgField = t.GetField("_mainMenuCG", any);
            if (menusField == null || listField == null || visibleMethod == null) { broken = true; Core.Inst.LoggerInstance.Warning("menu library fix off: library changed"); return; }
        }
        Apply();
    }

    // also from the mods' OnLateUpdate, after everything else this frame
    internal static void Apply()
    {
        if (broken || menusField == null || menusField.GetValue(null) is not IList menus) return;
        object open = null;
        foreach (var m in menus)
            if (m != null && (bool)visibleMethod.Invoke(m, null)) { open = m; break; }
        if (open == null) return;

        // each page also shows or hides the game's pause buttons by whether *its own* page is open, so a closed
        // page puts them back (usable, under the open one). any page open: they stay hidden and off
        if (mainCgField != null)
            foreach (var m in menus)
                if (m != null && mainCgField.GetValue(m) is UnityEngine.CanvasGroup cg && cg != null)
                { cg.alpha = 0f; cg.interactable = false; cg.blocksRaycasts = false; }
        var menu = Il2Cpp.Menu.me;
        if (menu != null && menu.titleGroup != null) menu.titleGroup.alpha = 0f;

        if (kbVisible != null && (bool)kbVisible.GetValue(open) || KeyboardOpen()) return;   // an on-screen keyboard has the gamepad
        if (listField.GetValue(open) is Il2Cpp.MenuItemList list) Il2Cpp.MenuItemList.active = list;
    }

}
