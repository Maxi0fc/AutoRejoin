using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using InnerNet;

namespace AutoRejoin
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Among Us.exe")]
    public class AutoRejoinPlugin : BasePlugin
    {
        public static new ManualLogSource? Log;
        public static ConfigEntry<int>?   RejoinDelay;

        public static bool            PendingRejoin         = false;
        public static int             SavedGameId           = -1;
        public static EndGameManager? CurrentEndGameManager = null;
        public static string          ScreenText            = "";
        public static CountdownGui?   GuiObject             = null;

        private static RejoinBehaviour? _behaviour;
        private Harmony?                _harmony;

        public override void Load()
        {
            Log = base.Log;

            RejoinDelay = Config.Bind(
                "AutoRejoin", "RejoinDelay", 4,
                new ConfigDescription("Seconds to wait before clicking rejoin (4-10)",
                    new AcceptableValueRange<int>(4, 10))
            );

            ClassInjector.RegisterTypeInIl2Cpp<RejoinBehaviour>();
            ClassInjector.RegisterTypeInIl2Cpp<CountdownGui>();

            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loaded! Delay: {RejoinDelay.Value}s");
        }

        public static void TriggerRejoin()
        {
            if (_behaviour == null)
            {
                try
                {
                    var go = new GameObject("AutoRejoinBehaviour");
                    Object.DontDestroyOnLoad(go);
                    _behaviour = go.AddComponent<RejoinBehaviour>();
                    Log?.LogInfo("[AutoRejoin] RejoinBehaviour created.");
                }
                catch (System.Exception ex)
                {
                    Log?.LogError($"[AutoRejoin] Failed to create RejoinBehaviour: {ex.Message}");
                    return;
                }
            }
            _behaviour.StartRejoin();
        }

        public static void CancelRejoin()
        {
            _behaviour?.Cancel();
            PendingRejoin         = false;
            SavedGameId           = -1;
            CurrentEndGameManager = null;
            ScreenText            = "";
        }
    }

    // ─────────────────────────────────────────────────────
    //  MonoBehaviour — countdown then one click
    // ─────────────────────────────────────────────────────
    public class RejoinBehaviour : MonoBehaviour
    {
        public RejoinBehaviour(System.IntPtr ptr) : base(ptr) { }

        private bool   _running  = false;
        private float  _timer    = 0f;
        private int    _lastShown = -1;
        private string _gameCode = "";

        public void StartRejoin()
        {
            _gameCode  = GameCode.IntToGameName(AutoRejoinPlugin.SavedGameId);
            _timer     = AutoRejoinPlugin.RejoinDelay?.Value ?? 4;
            _lastShown = -1;
            _running   = true;
            AutoRejoinPlugin.Log?.LogInfo($"[AutoRejoin] Countdown started ({_timer}s) for: {_gameCode}");
        }

        public void Cancel()
        {
            _running = false;
            AutoRejoinPlugin.ScreenText = "";
        }

        private void Update()
        {
            if (!_running) return;

            _timer -= Time.unscaledDeltaTime;
            int secs = Mathf.Max(1, Mathf.CeilToInt(_timer));

            if (secs != _lastShown)
            {
                _lastShown = secs;
                AutoRejoinPlugin.ScreenText = $"[AutoRejoin]  Rejoining in {secs}s  ({_gameCode})";
                AutoRejoinPlugin.Log?.LogInfo(AutoRejoinPlugin.ScreenText);
            }

            if (_timer <= 0f)
            {
                _running = false;
                AutoRejoinPlugin.ScreenText = "";
                ClickOnce();
            }
        }

        private void ClickOnce()
        {
            AutoRejoinPlugin.Log?.LogInfo("[AutoRejoin] Looking for PlayAgainButton...");

            var buttons = Object.FindObjectsOfType<PassiveButton>();
            foreach (var btn in buttons)
            {
                if (btn.gameObject.name == "PlayAgainButton")
                {
                    AutoRejoinPlugin.Log?.LogInfo("[AutoRejoin] Clicking PlayAgainButton.");
                    try
                    {
                        btn.OnClick.Invoke();
                        AutoRejoinPlugin.Log?.LogInfo("[AutoRejoin] Clicked! Waiting for host...");
                    }
                    catch (System.Exception ex)
                    {
                        AutoRejoinPlugin.Log?.LogWarning($"[AutoRejoin] Click failed: {ex.Message}");
                        // Fallback
                        try { btn.ReceiveClickDown(); btn.ReceiveClickUp(); }
                        catch { }
                    }
                    return;
                }
            }

            AutoRejoinPlugin.Log?.LogWarning("[AutoRejoin] PlayAgainButton not found after countdown.");
        }
    }

    // ─────────────────────────────────────────────────────
    //  CountdownGui — draws text on screen via OnGUI
    // ─────────────────────────────────────────────────────
    public class CountdownGui : MonoBehaviour
    {
        public CountdownGui(System.IntPtr ptr) : base(ptr) { }

        private GUIStyle? _style;
        private GUIStyle? _shadow;

        private void BuildStyles()
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            _style.normal.textColor  = new Color(0f, 0.85f, 1f, 1f);
            _shadow = new GUIStyle(_style);
            _shadow.normal.textColor = new Color(0f, 0f, 0f, 0.65f);
        }

        private void OnGUI()
        {
            string text = AutoRejoinPlugin.ScreenText;
            if (string.IsNullOrEmpty(text)) return;
            if (_style == null) BuildStyles();

            float w = Screen.width * 0.7f;
            float h = 44f;
            float x = (Screen.width - w) / 2f;
            float y = Screen.height - 80f;

            GUI.Label(new Rect(x + 1f, y + 1f, w, h), text, _shadow);
            GUI.Label(new Rect(x,      y,       w, h), text, _style);
        }
    }

    // ─────────────────────────────────────────────────────
    //  PATCH — EndGameManager.Start → trigger rejoin
    // ─────────────────────────────────────────────────────
    [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
    public static class Patch_EndGameStart
    {
        public static void Postfix(EndGameManager __instance)
        {
            if (AmongUsClient.Instance == null) return;

            AutoRejoinPlugin.PendingRejoin         = true;
            AutoRejoinPlugin.SavedGameId           = AmongUsClient.Instance.GameId;
            AutoRejoinPlugin.CurrentEndGameManager = __instance;

            AutoRejoinPlugin.Log?.LogInfo($"[AutoRejoin] Game ended. Code: {GameCode.IntToGameName(AutoRejoinPlugin.SavedGameId)}");

            // Create GUI here while scene is active
            if (AutoRejoinPlugin.GuiObject == null)
            {
                var go = new GameObject("AutoRejoinGUI");
                Object.DontDestroyOnLoad(go);
                AutoRejoinPlugin.GuiObject = go.AddComponent<CountdownGui>();
                AutoRejoinPlugin.Log?.LogInfo("[AutoRejoin] CountdownGui created.");
            }

            AutoRejoinPlugin.TriggerRejoin();
        }
    }

    // ─────────────────────────────────────────────────────
    //  PATCH — LobbyBehaviour.Start → we made it, cancel
    // ─────────────────────────────────────────────────────
    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
    public static class Patch_LobbyStart
    {
        public static void Postfix()
        {
            if (!AutoRejoinPlugin.PendingRejoin) return;
            AutoRejoinPlugin.Log?.LogInfo("[AutoRejoin] Back in lobby — done.");
            AutoRejoinPlugin.CancelRejoin();
        }
    }

    // ─────────────────────────────────────────────────────
    //  PATCH — AmongUsClient.ExitGame → manual leave
    // ─────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
    public static class Patch_ExitGame
    {
        public static void Prefix()
        {
            if (AutoRejoinPlugin.PendingRejoin)
            {
                AutoRejoinPlugin.Log?.LogInfo("[AutoRejoin] Manual exit — cancelling.");
                AutoRejoinPlugin.CancelRejoin();
            }
        }
    }
}

namespace AutoRejoin
{
    internal static class PluginInfo
    {
        public const string PLUGIN_GUID    = "com.community.autorejoin";
        public const string PLUGIN_NAME    = "AutoRejoin";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
