using System;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeamlessSwap.Game;

namespace SeamlessSwap
{
    public enum BypassKey
    {
        Control,
        Alt,
        None,
    }

    // Soft dependencies only order the load; none of these mods is required, and nothing here
    // references their types. Harmony priorities are not set anywhere: see docs/COMPATIBILITY.md.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(UiFixesGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PackNStrapGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(FikaGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SeamlessSwapPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.joelhauser.seamlessswap";
        public const string PluginName = "Seamless Swap";
        public const string PluginVersion = "0.0.1";

        public const string UiFixesGuid = "com.tyfon.uifixes";
        public const string PackNStrapGuid = "com.wtt.packnstrap";
        public const string FikaGuid = "com.fika.core";

        internal static ConfigEntry<bool> EnableBackpackSwaps;
        internal static ConfigEntry<bool> EnableRigSwaps;
        internal static ConfigEntry<BypassKey> Bypass;
        internal static ConfigEntry<bool> DiagnosticLogging;

        internal static ManualLogSource Log;

        /// <summary>Fika's inventory flow is host-authoritative and untested with this mod, so swaps stay off.</summary>
        internal static bool FikaInstalled;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            EnableBackpackSwaps = Config.Bind("Swaps", "Enable backpack swaps", true,
                "Dragging a body's backpack onto your own transfers your belongings. Takes effect immediately.");
            EnableRigSwaps = Config.Bind("Swaps", "Enable rig swaps", true,
                "Dragging a body's rig onto your own transfers your belongings. Takes effect immediately.");
            Bypass = Config.Bind("Swaps", "Bypass modifier", BypassKey.Control,
                "Hold while dropping to get the normal game behavior for that drag, including UI Fixes if installed. " +
                "Checked at the moment of the drop. Shift is not offered because UI Fixes uses it to suppress drags, and " +
                "Alt is UI Fixes' default Force Swap key.");
            DiagnosticLogging = Config.Bind("Debug", "Diagnostic logging", false,
                "Writes planning details to the BepInEx log. Takes effect immediately.");

            FikaInstalled = Chainloader.PluginInfos.ContainsKey(FikaGuid);
            Logger.LogInfo($"{PluginName} {PluginVersion}: UI Fixes {Describe(UiFixesGuid)}, Pack 'n' Strap {Describe(PackNStrapGuid)}, Fika {Describe(FikaGuid)}");
            if (FikaInstalled)
            {
                Logger.LogWarning("Fika is installed: automatic swaps are off and drags keep their normal behavior. This combination has not been tested.");
            }

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(SlotViewCanAcceptPatch));
                _harmony.PatchAll(typeof(SlotViewAcceptItemPatch));
            }
            catch (Exception ex)
            {
                // Half-patched would mean a hover that promises a drop that does nothing.
                Logger.LogError("Could not patch the game; Seamless Swap is off. " + ex);
                _harmony?.UnpatchSelf();
            }
        }

        private static string Describe(string guid)
        {
            return Chainloader.PluginInfos.TryGetValue(guid, out var info) ? info.Metadata.Version.ToString() : "absent";
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
