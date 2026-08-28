using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using ModsPanel;
using Newtonsoft.Json.Linq;
using SteamShelf;
using SteamShelf.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SteamGridBox;

public sealed class SteamGridBoxMod : MelonMod
{
    internal const string OwnerId = "Rusty.SteamGridBox";
    internal static SteamGridBoxMod Instance { get; private set; }
    private ModConfig config;
    private bool requestRunning;
    private bool bulkRunning;

    public override void OnInitializeMelon()
    {
        Instance = this;
        config = ModConfig.Load();
        RegisterSettings();
        LoggerInstance.Msg($"Config: {ModConfig.FilePath}");
    }

    private void RegisterSettings()
    {
        ModsPanelApi.RegisterSection(OwnerId, "SteamGridBox", 130).Clear()
            .AddText("api-key", "SteamGridDB API key", () => config.ApiKey, value =>
            {
                config.ApiKey = (value ?? "").Trim();
                config.Save();
            }, "Paste your SteamGridDB API key")
            .AddToggle("skip-existing", "Skip games that already have cover art", () => config.SkipExistingCoverArt, value =>
            {
                config.SkipExistingCoverArt = value;
                config.Save();
            })
            .AddButton("grab-all", "Download covers for every positive Steam App ID", "Grab All Art", StartGrabAll)
            .AddLabel("help", "Grab All Art selects SteamGridDB's first ranked 600x900 static cover. Existing loaded or cached covers are skipped when the checkbox is enabled. Rate limits pause the run and retry automatically.");
    }

    private void StartGrabAll()
    {
        if (bulkRunning)
        {
            ModsUi.ShowToast("Grab All Art is already running.");
            return;
        }
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            ModsUi.ShowToast("Add your SteamGridDB API key first.");
            return;
        }

        List<SteamGameData> games = SteamLibrarySystem.GetKnownGames()
            .Where(game => game != null && game.AppId > 0)
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (games.Count == 0)
        {
            ModsUi.ShowToast("No positive Steam App ID games are currently loaded.");
            return;
        }
        MelonCoroutines.Start(GrabAll(games));
    }

    private IEnumerator GrabAll(List<SteamGameData> games)
    {
        bulkRunning = true;
        int applied = 0;
        int skipped = 0;
        int failed = 0;
        ModsUi.ShowToast($"Grab All Art started for {games.Count} games.");

        for (int index = 0; index < games.Count; index++)
        {
            SteamGameData game = games[index];
            if (index == 0 || (index + 1) % 10 == 0)
                ModsUi.ShowToast($"Grab All Art: {index + 1}/{games.Count} • applied {applied} • skipped {skipped} • failed {failed}", 6f);

            if (config.SkipExistingCoverArt && HasExistingCover(game))
            {
                skipped++;
                continue;
            }

            JArray grids = null;
            yield return FetchGridData(game, (data, _) => grids = data);
            string imageUrl = (string)grids?.FirstOrDefault()?["url"];
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                skipped++;
                yield return new WaitForSecondsRealtime(0.25f);
                continue;
            }

            using UnityWebRequest download = UnityWebRequest.Get(imageUrl);
            download.timeout = 30;
            yield return download.SendWebRequest();
            byte[] bytes = download.result == UnityWebRequest.Result.Success ? download.downloadHandler.data : null;
            if (!LooksLikeImage(bytes))
            {
                failed++;
                yield return new WaitForSecondsRealtime(0.25f);
                continue;
            }

            if (ApplyCover(bytes, game, null, false)) applied++;
            else failed++;
            yield return new WaitForSecondsRealtime(0.25f);
        }

        bulkRunning = false;
        ModsUi.ShowToast($"Grab All Art finished: {applied} applied, {skipped} skipped, {failed} failed.", 10f);
    }

    private static bool HasExistingCover(SteamGameData game)
    {
        if (game.BoxArtLoaded) return true;
        string path = Path.Combine(Application.persistentDataPath, "steam_cache_v2", game.AppId.ToString(), "boxart.jpg");
        try { return File.Exists(path) && new FileInfo(path).Length > 64; }
        catch { return false; }
    }

    private static float GetRetryDelay(UnityWebRequest request)
    {
        string value = request.GetResponseHeader("Retry-After");
        if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float seconds))
            return Mathf.Clamp(seconds, 1f, 900f);
        if (DateTimeOffset.TryParse(value, out DateTimeOffset retryAt))
            return Mathf.Clamp((float)(retryAt - DateTimeOffset.UtcNow).TotalSeconds, 1f, 900f);
        return 60f;
    }

    internal void OpenFor(Menu_Inspect menu)
    {
        if (requestRunning) return;
        BoxInspector inspector = AccessTools.Field(typeof(Menu_Inspect), "m_boxInspector").GetValue(menu) as BoxInspector;
        SteamGameData game = inspector?.heldInfo;
        if (game == null) return;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            ModsUi.ShowToast("Add your SteamGridDB API key in Mod Settings first.");
            return;
        }
        MelonCoroutines.Start(FetchChoices(game, inspector));
    }

    private IEnumerator FetchChoices(SteamGameData game, BoxInspector inspector)
    {
        requestRunning = true;
        ModsUi.ShowToast($"Loading SteamGridDB covers for {game.Name}...");
        JArray data = null;
        bool matchedByName = false;
        yield return FetchGridData(game, (result, usedNameFallback) =>
        {
            data = result;
            matchedByName = usedNameFallback;
        });
        requestRunning = false;

        List<GridChoice> choices;
        try
        {
            choices = data?.Take(24).Select(token => GridChoice.FromJson(token, matchedByName)).Where(x => x != null).ToList() ?? new List<GridChoice>();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error(ex);
            ModsUi.ShowToast("SteamGridDB returned an unreadable response.");
            yield break;
        }

        if (choices.Count == 0)
        {
            ModsUi.ShowToast("No 600x900 SteamGridDB covers were found.");
            yield break;
        }

        ModMenu picker = ModsUi.CreateMenu(OwnerId, "SteamGridDB Covers", game.Name);
        picker.Eyebrow = "CHOOSE COVER";
        picker.AddLabel("Choose a 600x900 cover. Results are ordered by SteamGridDB's ranking.");
        for (int i = 0; i < choices.Count; i++)
        {
            GridChoice choice = choices[i];
            int number = i + 1;
            picker.AddButton($"Cover {number}", () => MelonCoroutines.Start(DownloadAndPreview(choice, game, inspector, picker)), choice.Detail);
        }
        picker.Show();
    }

    private IEnumerator FetchGridData(SteamGameData game, Action<JArray, bool> completed)
    {
        const string filters = "?dimensions=600x900&types=static&nsfw=false&humor=false";
        JArray grids = null;
        yield return GetApiData($"https://www.steamgriddb.com/api/v2/grids/steam/{game.AppId}{filters}", data => grids = data);
        if (grids != null && grids.Count > 0)
        {
            completed(grids, false);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(game.Name))
        {
            completed(grids ?? new JArray(), false);
            yield break;
        }

        JArray matches = null;
        string escapedName = UnityWebRequest.EscapeURL(game.Name);
        yield return GetApiData($"https://www.steamgriddb.com/api/v2/search/autocomplete/{escapedName}", data => matches = data);
        JToken match = matches?.FirstOrDefault(candidate => string.Equals(
            (string)candidate["name"], game.Name, StringComparison.OrdinalIgnoreCase)) ?? matches?.FirstOrDefault();
        int gameId = (int?)match?["id"] ?? 0;
        if (gameId <= 0)
        {
            completed(new JArray(), true);
            yield break;
        }

        JArray nameGrids = null;
        yield return GetApiData($"https://www.steamgriddb.com/api/v2/grids/game/{gameId}{filters}", data => nameGrids = data);
        completed(nameGrids ?? new JArray(), true);
    }

    private IEnumerator GetApiData(string endpoint, Action<JArray> completed)
    {
        while (true)
        {
            using UnityWebRequest request = UnityWebRequest.Get(endpoint);
            request.SetRequestHeader("Authorization", "Bearer " + config.ApiKey);
            request.timeout = 20;
            yield return request.SendWebRequest();
            if (request.responseCode == 429)
            {
                float waitSeconds = GetRetryDelay(request);
                ModsUi.ShowToast($"SteamGridDB rate limit reached. Waiting {Mathf.CeilToInt(waitSeconds)} seconds, then continuing...", Math.Min(10f, waitSeconds));
                yield return new WaitForSecondsRealtime(waitSeconds);
                continue;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                completed(null);
                yield break;
            }
            try { completed(JObject.Parse(request.downloadHandler.text)["data"] as JArray); }
            catch { completed(null); }
            yield break;
        }
    }

    private IEnumerator DownloadAndPreview(GridChoice choice, SteamGameData game, BoxInspector inspector, ModMenu picker)
    {
        if (requestRunning) yield break;
        requestRunning = true;
        ModsUi.ShowToast("Downloading selected cover...");
        using UnityWebRequest request = UnityWebRequest.Get(choice.Url);
        request.timeout = 30;
        yield return request.SendWebRequest();
        requestRunning = false;
        if (request.result != UnityWebRequest.Result.Success)
        {
            ModsUi.ShowToast("Cover download failed: " + FriendlyError(request));
            yield break;
        }

        byte[] bytes = request.downloadHandler.data;
        if (!LooksLikeImage(bytes))
        {
            ModsUi.ShowToast("SteamGridDB did not return a valid image.");
            yield break;
        }

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, bytes, false))
        {
            UnityEngine.Object.Destroy(texture);
            ModsUi.ShowToast("The downloaded cover could not be decoded.");
            yield break;
        }

        bool applied = false;
        ModMenu preview = ModsUi.CreateMenu(OwnerId + ".Preview", "Preview Cover", game.Name);
        preview.Eyebrow = "STEAMGRIDDB";
        preview.CloseText = "CANCEL";
        preview.AddImage(() => texture, 500f)
            .AddLabel(choice.Detail)
            .AddButton("Apply Cover", () =>
            {
                if (!ApplyCover(bytes, game, inspector, true)) return;
                applied = true;
                UnityEngine.Object.Destroy(texture);
                preview.Close();
            })
            .AddButton("Cancel", () => preview.Close());
        preview.Closed = () =>
        {
            if (!applied)
            {
                UnityEngine.Object.Destroy(texture);
                picker.Show();
            }
        };
        preview.Show();
    }

    private bool ApplyCover(byte[] bytes, SteamGameData game, BoxInspector inspector, bool showToast)
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "steam_cache_v2", game.AppId.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "boxart.jpg"), bytes);

            SteamTextureCache.EvictBoxArt(game.AppId);
            AccessTools.Field(typeof(SteamGameData), "<BoxArtBytes>k__BackingField").SetValue(game, bytes);
            AccessTools.Field(typeof(SteamGameData), "<BoxArtLoaded>k__BackingField").SetValue(game, true);
            inspector?.Box?.ApplyData(game);

            MethodInfo notify = AccessTools.Method(typeof(SteamLibrarySystem), "FireOnBoxArtUpdated");
            notify?.Invoke(null, new object[] { game });
            if (showToast) ModsUi.ShowToast("SteamGridDB cover applied and saved.");
            return true;
        }
        catch (Exception ex)
        {
            LoggerInstance.Error(ex);
            if (showToast) ModsUi.ShowToast("Could not apply the downloaded cover. See Latest.log.");
            return false;
        }
    }

    private static bool LooksLikeImage(byte[] bytes) => bytes != null && bytes.Length > 64 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8) ||
         (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
         (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46));

    private static string FriendlyError(UnityWebRequest request) => request.responseCode switch
    {
        401 => "API key rejected (401)",
        404 => "game not found (404)",
        429 => "rate limit reached (429)",
        _ => string.IsNullOrWhiteSpace(request.error) ? $"HTTP {request.responseCode}" : request.error
    };

    private sealed class GridChoice
    {
        internal string Url;
        internal string Detail;
        internal static GridChoice FromJson(JToken token, bool matchedByName)
        {
            string url = (string)token["url"];
            if (string.IsNullOrWhiteSpace(url)) return null;
            string style = (string)token["style"] ?? "cover";
            string author = (string)token["author"]?["name"] ?? "unknown artist";
            int width = (int?)token["width"] ?? 0;
            int height = (int?)token["height"] ?? 0;
            string fallback = matchedByName ? " • matched by name" : "";
            return new GridChoice { Url = url, Detail = $"{width}x{height} • {style} • {author}{fallback}" };
        }
    }
}

[HarmonyPatch(typeof(Menu_Inspect), "OnPreShow")]
internal static class InspectMenuPatch
{
    private const string ButtonName = "SteamGridBox_Button";

    private static void Postfix(Menu_Inspect __instance)
    {
        try
        {
            BoxInspector inspector = AccessTools.Field(typeof(Menu_Inspect), "m_boxInspector").GetValue(__instance) as BoxInspector;
            Transform[] descendants = __instance.GetComponentsInChildren<Transform>(true);
            GameObject addScreenshots = descendants.FirstOrDefault(x => x.name == "AddScreenshots")?.gameObject;
            GameObject changeArt = descendants.FirstOrDefault(x => x.name == "ChangeArt")?.gameObject;
            if (addScreenshots == null) return;

            Transform existing = addScreenshots.transform.parent.Find(ButtonName);
            GameObject buttonObject = existing != null
                ? existing.gameObject
                : UnityEngine.Object.Instantiate(addScreenshots, addScreenshots.transform.parent);
            buttonObject.name = ButtonName;
            buttonObject.SetActive(inspector?.heldInfo != null);
            if (existing == null)
            {
                RectTransform rect = buttonObject.GetComponent<RectTransform>();
                RectTransform addRect = addScreenshots.GetComponent<RectTransform>();
                RectTransform changeRect = changeArt?.GetComponent<RectTransform>();
                if (rect != null && addRect != null)
                {
                    float spacing = changeRect != null
                        ? changeRect.anchoredPosition.y - addRect.anchoredPosition.y
                        : Math.Max(64f, addRect.rect.height + 14f);
                    rect.anchoredPosition = addRect.anchoredPosition - new Vector2(0f, spacing);
                }
                TextMeshProUGUI label = buttonObject.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null) label.text = "SteamGrid";
                Button button = buttonObject.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick = new Button.ButtonClickedEvent();
                    button.onClick.AddListener(() => SteamGridBoxMod.Instance?.OpenFor(__instance));

                    Button addButton = addScreenshots.GetComponent<Button>();
                    if (addButton != null)
                    {
                        Navigation addNavigation = addButton.navigation;
                        addNavigation.mode = Navigation.Mode.Explicit;
                        addNavigation.selectOnDown = button;
                        addButton.navigation = addNavigation;

                        Navigation steamGridNavigation = button.navigation;
                        steamGridNavigation.mode = Navigation.Mode.Explicit;
                        steamGridNavigation.selectOnUp = addButton;
                        button.navigation = steamGridNavigation;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Melon<SteamGridBoxMod>.Logger.Error(ex);
        }
    }
}
