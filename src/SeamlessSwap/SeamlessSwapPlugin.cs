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
    [BepInDependency(BackpackGunSlotGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SeamlessSwapPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.seamlessswap";
        public const string PluginName = "Seamless Swap";
        public const string PluginVersion = "1.0.0";

        public const string UiFixesGuid = "com.tyfon.uifixes";
        public const string PackNStrapGuid = "com.wtt.packnstrap";
        public const string BackpackGunSlotGuid = "com.20fpsguy.backpackgunslot";
        public const string FikaGuid = "com.fika.core";

        internal static ConfigEntry<bool> EnableBackpackSwaps;
        internal static ConfigEntry<bool> EnableRigSwaps;
        internal static ConfigEntry<BypassKey> Bypass;
        internal static ConfigEntry<bool> DiagnosticLogging;
        internal static ConfigEntry<bool> FillAnimation;
        internal static ConfigEntry<float> FillSpeed;
        internal static ConfigEntry<bool> FillSounds;

        internal static ManualLogSource Log;

        /// <summary>Fika's inventory flow is host-authoritative and untested with this mod, so swaps stay off.</summary>
        internal static bool FikaInstalled;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            EnableBackpackSwaps = Config.Bind("Swaps", "Enable backpack swaps", true,
                "Dragging between a body's backpack and your own, either way, transfers your belongings. Takes effect immediately.");
            EnableRigSwaps = Config.Bind("Swaps", "Enable rig swaps", true,
                "Dragging between a body's rig and your own, either way, transfers your belongings. Takes effect immediately.");
            Bypass = Config.Bind("Swaps", "Bypass modifier", BypassKey.Control,
                "Hold while dropping to get the normal game behavior for that drag, including UI Fixes if installed. " +
                "Checked at the moment of the drop. Shift is not offered because UI Fixes uses it to suppress drags, and " +
                "Alt is UI Fixes' default Force Swap key.");
            FillAnimation = Config.Bind("Animation", "Fill animation", true,
                "After a swap, show items flying into their new spots. Only how it looks: items are already in place when it starts.");
            FillSounds = Config.Bind("Animation", "Fill sounds", true,
                "Play each item's own drop sound as it lands during the fill animation.");
            FillSpeed = Config.Bind("Animation", "Fill speed", 1f,
                new ConfigDescription("How fast items fly in. 1 is normal, 0.25 is slow, 4 is very fast.", new AcceptableValueRange<float>(0.25f, 4f)));
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
                _harmony.PatchAll(typeof(Curtain.SearchableItemViewShowPatch));
                _harmony.PatchAll(typeof(Immediate.ExecutePatch));
                BackpackGunSlotGuard.Apply(_harmony);
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
