using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FallTracker.Core), "FallTracker", "1.2.1", "waze3174")]
[assembly: MelonGame]
[assembly: MelonOptionalDependencies("BabyStepsMenuLib")]

namespace FallTracker;

// counts the pain: falls, metres lost, biggest drop, time on the ground, for this session and per save slot.
// shown on the pause menu; everything lives in UserData/FallTracker (never the game's files)
public class Core : MelonMod
{
    internal static Core Inst;
    static string dir;

    public class Stats
    {
        public int Falls { get; set; }
        public double MetresLost { get; set; }
        public double BiggestDrop { get; set; }
        public double SecondsDown { get; set; }
        public double LastDrop { get; set; }
        public double LastSeconds { get; set; }

        public void Add(double drop, double seconds)
        {
            Falls++;
            LastDrop = Math.Max(0, drop);
            LastSeconds = seconds;
            SecondsDown += seconds;
            if (drop > 0) { MetresLost += drop; BiggestDrop = Math.Max(BiggestDrop, drop); }
        }
    }

    readonly Stats session = new Stats();
    Stats slotStats; int statsSlot = -1;

    public override void OnInitializeMelon()
    {
        Inst = this;
        dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FallTracker");
        Directory.CreateDirectory(dir);
        var cat = MelonPreferences.CreateCategory("FallTracker");
        cat.SetFilePath(Path.Combine(dir, "FallTracker.cfg"));
        ShowPanel = cat.CreateEntry("ShowPanel", true, "Show stats", "Show the stats on the pause menu.");
        // our tabs for the pause menu page (needs BabyStepsMenuLib); built once every mod has started
        try { RegisterMenu(); menuOk = true; }
        catch (Exception e) when (e is FileNotFoundException || e is TypeLoadException || e is MissingMethodException) { }
    }

    static void RegisterMenu() => FallMenu.Announce();

    internal static MelonPreferences_Entry<bool> ShowPanel;
    internal static string ReportsDir => Path.Combine(dir, "reports");

    public override void OnLateInitializeMelon()
    {
        // the pause menu page (rage reports, options) needs BabyStepsMenuLib; without it the rest works as before
        if (!menuOk) { LoggerInstance.Msg("BabyStepsMenuLib not installed: no pause menu page"); return; }
        try { BuildMenu(); }
        catch (Exception e) when (e is FileNotFoundException || e is TypeLoadException || e is MissingMethodException)
        { LoggerInstance.Msg("BabyStepsMenuLib not installed: no FALL TRACKER page"); }
    }

    static bool menuOk;
    void BuildMenu() => FallMenu.Build(LoggerInstance, HarmonyInstance);

    public override void OnLateUpdate()
    {
        if (menuOk) try { HubLateUpdate(); } catch (Exception e) { LoggerInstance.Error("menu: " + e.Message); }
    }

    static void HubLateUpdate() => MenuHub.LateUpdate();
    static void MenuUpdate() => FallMenu.Update();

    // ---- watching nate ----

    const float TeleportDist = 20f;   // a jump this big in one step while standing = teleport (load, quicksave, cutscene), not a fall
    const float RespawnDist = 30f;    // getting up this far from where you went down = the game moved you (loading a save, teleport): not a fall
    const float ArmAfter = 1f;        // seconds standing on both feet before falls count (while a save loads nate stands at the origin for a moment)

    Il2Cpp.PlayerMovement pm;
    bool armed;                 // stood ArmAfter seconds since this nate appeared (a loaded save can start down)
    float standingFor;
    bool countThis;             // the current fall started while armed
    bool down;
    bool upPending;             // got up, waiting for both feet on the ground to measure where we ended
    Vector3 standPos;           // feet on the ground, last time both were planted
    Vector3 lastHip;
    Vector3 fallStart;
    float fallTime;

    public override void OnFixedUpdate()
    {
        try { Watch(); } catch (Exception e) { LoggerInstance.Error("watch: " + e.Message); pm = null; }
    }

    void Watch()
    {
        var now = Il2Cpp.PlayerMovement.me;
        if (now == null) { pm = null; return; }
        if (pm == null || pm.Pointer != now.Pointer) { pm = now; armed = false; standingFor = 0; down = false; upPending = false; lastHip = Hip(); }

        var hip = Hip();
        bool dead = pm.dead;
        bool jumped = (hip - lastHip).magnitude > TeleportDist;
        lastHip = hip;

        if (!dead && Planted(out var ground))
        {
            if (jumped && !down) { armed = false; standingFor = 0; }   // teleported: start over from here
            if (upPending) { upPending = false; Landed(ground); }
            standPos = ground;
            standingFor += Time.fixedDeltaTime;
            if (standingFor >= ArmAfter) armed = true;
        }
        else standingFor = 0;

        if (dead && !down)
        {
            down = true;
            countThis = armed;
            fallStart = standPos;
            fallTime = Time.time;
        }
        else if (!dead && down)
        {
            down = false;
            if (countThis) upPending = true;
        }
    }

    void Landed(Vector3 ground)
    {
        double secs = Time.time - fallTime;
        float moved = (ground - fallStart).magnitude;
        if (moved > RespawnDist) { LoggerInstance.Msg($"not counted: got up {moved:F0} m from where nate went down (load / teleport)"); return; }
        double drop = fallStart.y - ground.y;
        session.Add(drop, secs);
        var s = SlotStats();
        s.Add(drop, secs);
        Save();
        AppendFall(fallStart, drop, secs);
        LoggerInstance.Msg($"fell: {(drop > 0.05 ? $"{drop:F1} m lost" : "no height lost")}, {secs:F1} s down (session {Falls(session.Falls)}, {session.MetresLost:F1} m)");
    }

    Vector3 Hip() => pm.hipPivot != null ? pm.hipPivot.position : Vector3.zero;

    // both feet down: the lower foot is where you stand
    bool Planted(out Vector3 ground)
    {
        ground = default;
        var f = pm.feet;
        if (f == null || f.Length < 2 || f[0] == null || f[1] == null || !f[0].planted || !f[1].planted) return false;
        var a = f[0].targetPos; var b = f[1].targetPos;
        ground = a.y < b.y ? a : b;
        return true;
    }

    // ---- storage: UserData/FallTracker/slot<N>.json + slot<N>-falls.csv ----

    static int Slot => Il2Cpp.SaveGod.me != null ? Il2Cpp.SaveGod.me.slot : 0;

    Stats SlotStats()
    {
        int slot = Slot;
        if (slotStats != null && statsSlot == slot) return slotStats;
        statsSlot = slot;
        slotStats = null;
        string p = Path.Combine(dir, $"slot{slot + 1}.json");
        try { if (File.Exists(p)) slotStats = JsonSerializer.Deserialize<Stats>(File.ReadAllText(p)); }
        catch (Exception e) { LoggerInstance.Warning($"couldn't read {p}: {e.Message}"); }
        return slotStats ??= new Stats();
    }

    void Save()
    {
        try { File.WriteAllText(Path.Combine(dir, $"slot{statsSlot + 1}.json"), JsonSerializer.Serialize(slotStats, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { LoggerInstance.Warning("couldn't save stats: " + e.Message); }
    }

    // where each fall started, for a heatmap later
    void AppendFall(Vector3 at, double drop, double secs)
    {
        try
        {
            string p = Path.Combine(dir, $"slot{statsSlot + 1}-falls.csv");
            if (!File.Exists(p)) File.WriteAllText(p, "time,x,y,z,drop,secondsDown\n");
            var ci = CultureInfo.InvariantCulture;
            File.AppendAllText(p, string.Format(ci, "{0:yyyy-MM-dd HH:mm:ss},{1:F1},{2:F1},{3:F1},{4:F2},{5:F1}\n", DateTime.Now, at.x, at.y, at.z, drop, secs));
        }
        catch (Exception e) { LoggerInstance.Warning("couldn't log the fall: " + e.Message); }
    }

    // ---- pause menu panel ----

    TextMeshProUGUI panel;

    public override void OnUpdate()
    {
        try { Panel(); } catch (Exception e) { LoggerInstance.Error("panel: " + e.Message); }
        if (menuOk) try { MenuUpdate(); } catch (Exception e) { menuOk = false; LoggerInstance.Error("menu: " + e.Message); }
    }

    void Panel()
    {
        var m = Il2Cpp.Menu.me;
        bool show = ShowPanel.Value && m != null && m.paused && (int)m.currentMenuScreen == 0 && MainButtonsVisible(m) && pm != null && armed && Il2Cpp.PlayerMovement.me != null;
        if (!show) { if (panel != null) panel.gameObject.SetActive(false); return; }
        if (panel == null && !MakePanel(m)) return;
        panel.gameObject.SetActive(true);
        var s = SlotStats();
        panel.text =
            Row("", "SESSION", "THIS SAVE") +
            Row("FALLS", session.Falls.ToString(), s.Falls.ToString()) +
            Row("METRES LOST", M(session.MetresLost), M(s.MetresLost)) +
            Row("LAST FALL", Last(session), Last(s)) +
            Row("BIGGEST DROP", M(session.BiggestDrop), M(s.BiggestDrop)) +
            Row("ON THE GROUND", T(session.SecondsDown), T(s.SecondsDown));
    }

    // the menu font isn't monospaced: columns by position
    // other mods' pages (BabyStepsMenuLib) replace the pause buttons while the game still says "main screen":
    // only show while the game's own CONTINUE button is actually visible
    static bool MainButtonsVisible(Il2Cpp.Menu m)
    {
        var b = m.ContinueGameButton;
        if (b == null || !b.gameObject.activeInHierarchy) return false;
        float a = 1f;
        for (var t = b.transform; t != null; t = t.parent)
        {
            var cg = t.GetComponent<CanvasGroup>();
            if (cg != null) a *= cg.alpha;
        }
        return a > 0.5f;
    }

    static string Falls(int n) => n == 1 ? "1 fall" : $"{n} falls";
    static string Row(string name, string a, string b) => $"{name}<pos=300>{a}<pos=480>{b}\n";
    static string Last(Stats s) => s.Falls == 0 ? "-" : $"{M(s.LastDrop)}, {T(s.LastSeconds)}";
    static string M(double m) => m.ToString("0.0", CultureInfo.InvariantCulture) + " M";
    static string T(double s) => s < 60 ? $"{s:0}S" : s < 3600 ? $"{(int)(s / 60)}M {(int)(s % 60):00}S" : $"{(int)(s / 3600)}H {(int)(s % 3600 / 60):00}M";

    // our own overlay canvas, left side between the game's title and its menu (a minimap mod uses the top right) (a minimap mod uses the top right)
    bool MakePanel(Il2Cpp.Menu m)
    {
        // styled like the pause menu's own buttons (Continue), falling back to the save screen's text
        TMP_Text src = m.ContinueGameButtonText;
        if (src == null && m.slotText != null && m.slotText.Length > 0) src = m.slotText[0];
        if (src == null) return false;
        var go = new GameObject("FallTracker panel");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = go.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        var t = new GameObject("text");
        t.transform.SetParent(go.transform, false);
        panel = t.AddComponent<TextMeshProUGUI>();
        panel.font = src.font;
        panel.fontSharedMaterial = src.fontSharedMaterial;
        panel.fontSize = 26;
        // the menu's text is white; its buttons tint it (pale cyan when not selected): use the unselected tint
        var c = src.color;
        var btn = m.ContinueGameButton;
        if (btn != null) { var cb = btn.colors; c *= cb.normalColor * cb.colorMultiplier; c.a = Mathf.Min(c.a, 1f); }
        panel.color = c;
        panel.fontStyle = src.fontStyle;
        panel.characterSpacing = src.characterSpacing;
        panel.wordSpacing = src.wordSpacing;
        panel.alignment = TextAlignmentOptions.TopLeft;
        panel.enableWordWrapping = false;
        panel.raycastTarget = false;
        var rt = panel.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(90f, -250f);   // under the title, above CONTINUE, lined up with the menu (1080 high reference)
        rt.sizeDelta = new Vector2(660f, 300f);
        return true;
    }

    // ---- rage report: written when the session ends ----

    public override void OnApplicationQuit() => Report();

    void Report()
    {
        if (session.Falls == 0) return;
        try
        {
            string rd = ReportsDir;
            Directory.CreateDirectory(rd);
            var s = SlotStats();
            string text =
                $"rage report {DateTime.Now:yyyy-MM-dd HH:mm}\n\n" +
                $"this session: {Falls(session.Falls)}, {M(session.MetresLost).ToLowerInvariant()} lost, biggest drop {M(session.BiggestDrop).ToLowerInvariant()}, {T(session.SecondsDown).ToLowerInvariant()} on the ground\n" +
                $"save slot {statsSlot + 1} so far: {Falls(s.Falls)}, {M(s.MetresLost).ToLowerInvariant()} lost, biggest drop {M(s.BiggestDrop).ToLowerInvariant()}, {T(s.SecondsDown).ToLowerInvariant()} on the ground\n";
            File.WriteAllText(Path.Combine(rd, $"{DateTime.Now:yyyy-MM-dd_HHmm}.txt"), text);
        }
        catch (Exception e) { LoggerInstance.Warning("rage report: " + e.Message); }
    }
}
