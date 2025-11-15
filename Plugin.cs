using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using GorillaNetworking;
using HarmonyLib;
using Newtonsoft.Json;
using Photon.Pun;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace TooMuchInfo;

[BepInPlugin(Constants.GUID, Constants.Name, Constants.Version)]
public class Plugin : BaseUnityPlugin
{
    private static Dictionary<string, string[]> specialModsList      = new();
    private static Dictionary<string, string>   specialPlayers       = new();
    private static Dictionary<string, string[]> specialCosmeticsList = new();
    private static          HashSet<string>              blacklisted     = new();
    private static readonly Dictionary<string, string>   datePool        = new();

    private async void Awake()
    {
        await LoadListsFromGitHub();

        HarmonyPatches.ApplyHarmonyPatches();

        PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable
        {
                { Constants.HashKey, Constants.Version },
        });
    }

    private async Task LoadListsFromGitHub()
    {
        try
        {
            using HttpClient client = new();

            string modsUrl =
                    "https://raw.githubusercontent.com/ZlothY29IQ/GorillaInfo/refs/heads/main/KnownMods.txt";

            string playersUrl =
                    "https://raw.githubusercontent.com/ZlothY29IQ/GorillaInfo/refs/heads/main/KnownPeople.txt";

            string blacklistUrl =
                    "https://raw.githubusercontent.com/ZlothY29IQ/GorillaInfo/refs/heads/main/Blacklisted.txt";

            string cosmeticsUrl =
                    "https://raw.githubusercontent.com/ZlothY29IQ/GorillaInfo/refs/heads/main/SpecialCosmetics.txt";

            string modsJson      = await client.GetStringAsync(modsUrl);
            string playersJson   = await client.GetStringAsync(playersUrl);
            string blacklistJson = await client.GetStringAsync(blacklistUrl);
            string cosmeticsJson = await client.GetStringAsync(cosmeticsUrl);

            List<ModEntry> modsListObjects =
                    JsonConvert.DeserializeObject<List<ModEntry>>(modsJson);

            List<PlayerEntry> playersListObjects =
                    JsonConvert.DeserializeObject<List<PlayerEntry>>(playersJson);

            List<string> blacklistedIds =
                    JsonConvert.DeserializeObject<List<string>>(blacklistJson);

            List<CosmeticEntry> cosmeticsListObjects =
                    JsonConvert.DeserializeObject<List<CosmeticEntry>>(cosmeticsJson);

            specialModsList      = modsListObjects.ToDictionary(x => x.Key, x => x.Value);
            specialPlayers       = playersListObjects.ToDictionary(x => x.Key, x => x.Value);
            blacklisted          = new HashSet<string>(blacklistedIds);
            specialCosmeticsList = cosmeticsListObjects.ToDictionary(x => x.Key, x => x.Value);

            Logger.LogInfo("Special lists loaded from GitHub successfully.");
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to load special lists from GitHub: " + ex.Message);
        }
    }


    private static string CheckCosmetics(VRRig rig)
    {
        string specialties = "";

        foreach (KeyValuePair<string, string[]> specialCosmetic in specialCosmeticsList)
        {
            if (rig.concatStringOfCosmeticsAllowed.Contains(specialCosmetic.Key))
            {
                specialties += (specialties == "" ? "" : ", ") +
                               $"<color=#{specialCosmetic.Value[1]}>{specialCosmetic.Value[0]}</color>";
            }
        }

        return specialties == "" ? null : specialties;
    }

    private static string CheckMods(VRRig rig)
    {
        string    specialMods = "";
        NetPlayer creator     = rig.Creator;

        Hashtable props = creator.GetPlayerRef().CustomProperties;
        foreach (DictionaryEntry prop in props)
        {
            string propKeyLower = prop.Key.ToString().ToLower();
            foreach (KeyValuePair<string, string[]> specialMod in specialModsList)
                if (propKeyLower == specialMod.Key.ToLower())
                    specialMods += (specialMods == "" ? "" : ", ") +
                                   $"<color=#{specialMod.Value[1]}>{specialMod.Value[0]}</color>";
        }

        CosmeticsController.CosmeticSet cosmeticSet = rig.cosmeticSet;
        foreach (CosmeticsController.CosmeticItem cosmetic in cosmeticSet.items)
            if (!cosmetic.isNullItem && !rig.concatStringOfCosmeticsAllowed.Contains(cosmetic.itemName))
            {
                specialMods += (specialMods == "" ? "" : ", ") + "<color=red>COSMETX</color>";

                break;
            }

        return specialMods == "" ? null : specialMods;
    }

    private static string CreationDate(VRRig rig)
    {
        string UserId = rig.Creator.UserId;

        if (datePool.ContainsKey(UserId))
            return datePool[UserId];

        datePool.Add(UserId, "LOADING");
        PlayFabClientAPI.GetAccountInfo(new GetAccountInfoRequest { PlayFabId = UserId, }, result =>
        {
            string date = result.AccountInfo.Created.ToString("MMM dd, yyyy").ToUpper();
            datePool[UserId] = date;
            rig.UpdateName();
        }, error =>
           {
               datePool[UserId] = "ERROR";
               rig.UpdateName();
           });

        return "LOADING";
    }

    private static string GetSpecialPlayerName(VRRig rig)
    {
        string userId = rig.Creator.UserId;

        if (specialPlayers.ContainsKey(userId))
            return $"<color=blue>{specialPlayers[userId]}</color>";

        return null;
    }

    private static string GetPlatform(VRRig rig)
    {
        string concatStringOfCosmeticsAllowed = rig.concatStringOfCosmeticsAllowed;

        if (concatStringOfCosmeticsAllowed.Contains("S. FIRST LOGIN"))
            return "STEAM";

        if (concatStringOfCosmeticsAllowed.Contains("FIRST LOGIN") ||
            rig.Creator.GetPlayerRef().CustomProperties.Count >= 2)
            return "PC";

        return "QUEST?";
    }

    private static void LogUnknownSpecialCosmetic(string userId, string nickname, List<string> cosmeticIds)
    {
        string path         = Path.Combine(Paths.BepInExRootPath, "SpecialCosmeticsUnknownUserID.txt");
        string idLinePrefix = $"UserId={userId}, Nickname={nickname}";

        if (File.Exists(path))
            foreach (string existingLine in File.ReadAllLines(path))
                if (existingLine.Contains(idLinePrefix))
                    return;

        string line = $"{DateTime.Now}: {idLinePrefix}, Cosmetics=[{string.Join(", ", cosmeticIds)}]";
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string GetTurnSettings(VRRig rig)
    {
        Traverse turnType   = Traverse.Create(rig).Field("turnType");
        Traverse turnFactor = Traverse.Create(rig).Field("turnFactor");

        if (turnType != null && turnFactor != null)
        {
            string turnTypeValue = (string)turnType.GetValue();

            return turnTypeValue == "NONE" ? "NONE" : turnTypeValue + " " + turnFactor.GetValue();
        }

        return null;
    }

    private static int CountTotalCosmetics(VRRig rig)
    {
        if (string.IsNullOrEmpty(rig.concatStringOfCosmeticsAllowed))
            return 0;

        return rig.concatStringOfCosmeticsAllowed.Count(c => c == '.');
    }

    private static string FormatColor(Color color) =>
            "<color=red>"            + Math.Round(color.r * 9) +
            "</color> <color=green>" + Math.Round(color.g * 9) +
            "</color> <color=blue>"  + Math.Round(color.b * 9) + "</color>";

    public static void UpdateName(VRRig rig)
    {
        try
        {
            string    targetText = "Name";
            NetPlayer creator    = rig.Creator;

            if (creator != null)
            {
                string specialName = GetSpecialPlayerName(rig);
                string fullName    = creator.NickName;
                string userId      = creator.UserId;

                if (blacklisted.Contains(userId))
                {
                    string blockedText = $"{creator.NickName}\n<color=#964B00>Users Details Blocked</color>";
                    rig.playerText1.text = blockedText;

                    return;
                }

                if (specialName != null)
                    fullName = fullName + $" <color=purple>{specialName}</color>";

                string       modscount = CheckMods(rig);
                List<string> lines     = new();

                if (modscount != null)
                    lines.Add(""); // more space lol

                lines.Add("");
                lines.Add("");
                lines.Add("");
                lines.Add(fullName);

                string cosmetics = CheckCosmetics(rig);
                if (cosmetics != null)
                {
                    lines.Add(cosmetics);

                    List<string> foundSpecialCosmetics = new();
                    string[]     specialPrefixes = new[] { "LBADE.", "LBAAK.", "LBAAD.", "LBAGS.", "LMAPY.", "LBANI", };

                    foreach (string prefix in specialPrefixes)
                    {
                        foreach (string cosmeticId in rig.concatStringOfCosmeticsAllowed.Split(','))
                            if (cosmeticId.StartsWith(prefix))
                                foundSpecialCosmetics.Add(cosmeticId);
                    }

                    if (foundSpecialCosmetics.Count > 0 && !specialPlayers.ContainsKey(rig.Creator.UserId))
                        LogUnknownSpecialCosmetic(rig.Creator.UserId, rig.Creator.NickName, foundSpecialCosmetics);
                }

                string mods = CheckMods(rig);
                if (mods != null) lines.Add(mods);

                string creation = CreationDate(rig);
                if (creation != null) lines.Add(creation);

                string color = FormatColor(rig.playerColor);
                if (color != null) lines.Add(color);

                string turnSettings = GetTurnSettings(rig);
                if (turnSettings != null) lines.Add(turnSettings);

                string platform = GetPlatform(rig);
                if (platform != null) lines.Add(platform);

                targetText = string.Join("\n", lines);
            }

            Regex noRichText = new("<.*?>");
            rig.playerText1.text = targetText;
        }
        catch { }
    }

    private class ModEntry
    {
        public string   Key   { get; set; }
        public string[] Value { get; set; }
    }

    private class PlayerEntry
    {
        public string Key   { get; set; }
        public string Value { get; set; }
    }
    
    private class CosmeticEntry
    {
        public string   Key   { get; set; }
        public string[] Value { get; set; }
    }
}