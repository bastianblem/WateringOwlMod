using BepInEx;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using System.Reflection;
using System.Collections;
using BepInEx.Logging;
using HarmonyLib.Tools;
using System;

namespace WateringOwlMod
{
    [BepInPlugin("bs.wateringowl.6942067.aleandtale", "WateringOwlMod", "0.0.1")]
    public class WateringOwlMod : BaseUnityPlugin
    {
        void Awake()
        {
            Logger.LogInfo("WateringOwl wurde erfolgreich geladen!");
            Logger.LogMessage("WateringOwl made by blemdev & siluuu");

            // Test: Harmony-Instanz erstellen
            var harmony = new Harmony("bs.wateringowl.6942067.aleandtale");
            Logger.LogInfo($"Harmony-Instanz erstellt: {harmony.Id}");

            harmony.PatchAll(typeof(WateringOwlPatches));
        }
    }

    public class WateringOwlPatches
    {
        
    }
}