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

        if (kbVisible != null && (bool)kbVisible.GetValue(open)) return;   // its on-screen keyboard has the gamepad
        if (listField.GetValue(open) is Il2Cpp.MenuItemList list) Il2Cpp.MenuItemList.active = list;
    }

}
