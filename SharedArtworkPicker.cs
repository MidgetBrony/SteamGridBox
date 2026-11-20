using MelonLoader;
using ModsPanel;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace SteamGridBox;

public enum SteamGridArtworkKind
{
    Portrait,
    Square,
    Wide,
    Hero,
    Logo
}

public sealed class SteamGridArtworkRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string SearchQuery { get; set; } = string.Empty;
    public bool IncludeLogos { get; set; }
    public Action<byte[], SteamGridArtworkKind> Apply { get; set; }
    public Action Back { get; set; }
}

/// <summary>
/// Shared SteamGridDB browser for BOXROOM mods. SteamGridBox remains the sole
/// owner of credentials, HTTP requests, previews, loading state and navigation.
/// </summary>
public static class SteamGridArtworkBrowser
{
    public static bool Open(SteamGridArtworkRequest request)
    {
        if (request == null || request.Apply == null) return false;
        if (SteamGridBoxMod.Instance == null)
        {
            ModsUi.ShowToast("SteamGridBox is not loaded.", 7f);
            return false;
        }
        SharedArtworkPicker.Open(request);
        return true;
    }
}

internal static class SharedArtworkPicker
{
    private sealed class GameMatch
    {
        internal long Id;
        internal string Name;
        internal long ReleaseDate;
        internal bool Verified;
    }

    private sealed class ArtworkChoice
    {
        internal string Url;
        internal string Thumb;
        internal int Width;
        internal int Height;
        internal int Score;
        internal string Style;
        internal string Author;
    }

    private const string ApiRoot = "https://www.steamgriddb.com/api/v2";
    private static readonly Dictionary<string, string> SearchTerms = new(StringComparer.OrdinalIgnoreCase);
    private static Texture2D previewTexture;
    private static bool busy;
    private static int operationSerial;

    internal static void Open(SteamGridArtworkRequest request)
    {
        string id = StableId(request);
        string query = SearchTerms.TryGetValue(id, out string saved)
            ? saved
            : string.IsNullOrWhiteSpace(request.SearchQuery) ? request.Title : request.SearchQuery;
        SearchTerms[id] = query ?? string.Empty;

        ModMenu menu = ModsUi.CreateMenu(SteamGridBoxMod.OwnerId + ".art." + id,
            "SteamGridDB Artwork", request.Title);
        menu.Eyebrow = "STEAMGRIDBOX";
        menu.CloseText = "BACK";
        menu.AddTextInput("Game title", () => SearchTerms[id],
            value => SearchTerms[id] = value ?? string.Empty, "Title to search on SteamGridDB");
        if (HasApiKey)
            menu.AddButton("Search SteamGridDB", () => StartSearch(request),
                "Portrait, square, wide, hero" + (request.IncludeLogos ? " and transparent logo artwork" : " artwork"));
        menu.AddButton(HasApiKey ? "Change API key" : "Configure API key", () => OpenCredentials(request),
            "Managed once by SteamGridBox");
        menu.AddButton("Back", request.Back ?? ModsUi.CloseMenu);
        menu.AddLabel("SteamGridBox provides this shared artwork browser and API key to installed BOXROOM addons.");
        menu.Closed = request.Back;
        menu.Show();
    }

    private static bool HasApiKey => !string.IsNullOrWhiteSpace(SteamGridBoxMod.Instance?.ApiKey);

    private static void OpenCredentials(SteamGridArtworkRequest request)
    {
        string pending = SteamGridBoxMod.Instance?.ApiKey ?? string.Empty;
        ModMenu menu = ModsUi.CreateMenu(SteamGridBoxMod.OwnerId + ".art.credentials", "SteamGridDB Setup",
            "Shared by SteamGridBox");
        menu.Eyebrow = "STEAMGRIDBOX";
        menu.CloseText = "BACK";
        menu.AddPasswordInput("API key", () => pending, value => pending = value ?? string.Empty,
            "SteamGridDB developer API key");
        menu.AddButton("Save API key", () =>
        {
            SteamGridBoxMod.Instance.SetApiKey(pending);
            ModsUi.ShowToast(HasApiKey ? "SteamGridDB API key saved by SteamGridBox." : "Enter an API key first.");
            Open(request);
        }, "Used by SteamGridBox and its shared artwork browser");
        menu.AddButton("Back", () => Open(request));
        menu.Closed = () => Open(request);
        menu.Show();
    }

    private static void StartSearch(SteamGridArtworkRequest request)
    {
        if (busy) return;
        string query = SearchTerms.TryGetValue(StableId(request), out string value) ? value.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            ModsUi.ShowToast("Enter a game title to search.");
            return;
        }
        busy = true;
        int operation = ++operationSerial;
        ShowLoading(request, "Searching SteamGridDB", query, () => Open(request));
        MelonCoroutines.Start(SearchRoutine(request, query, operation));
    }

    private static IEnumerator SearchRoutine(SteamGridArtworkRequest request, string query, int operation)
    {
        JArray data = null;
        string error = null;
        yield return GetData(ApiRoot + "/search/autocomplete/" + UnityWebRequest.EscapeURL(query),
            (value, message) => { data = value; error = message; });
        if (operation != operationSerial) yield break;
        busy = false;
        if (data == null)
        {
            Open(request);
            ShowError(error);
            yield break;
        }

        List<GameMatch> matches = data.Take(30).Select(token => new GameMatch
        {
            Id = (long?)token["id"] ?? 0,
            Name = (string)token["name"] ?? "Unknown game",
            ReleaseDate = (long?)token["release_date"] ?? 0,
            Verified = (bool?)token["verified"] ?? false
        }).Where(value => value.Id > 0).ToList();
        ShowMatches(request, query, matches);
    }

    private static void ShowMatches(SteamGridArtworkRequest request, string query, IReadOnlyList<GameMatch> matches)
    {
        ModMenu menu = ModsUi.CreateMenu(SteamGridBoxMod.OwnerId + ".art.matches." + StableId(request),
            "SteamGridDB Matches", matches.Count + " result" + (matches.Count == 1 ? string.Empty : "s") + " for " + query);
        menu.Eyebrow = "STEAMGRIDBOX";
        menu.CloseText = "BACK";
        foreach (GameMatch match in matches.Take(20))
        {
            GameMatch selected = match;
            string year = selected.ReleaseDate > 0
                ? DateTimeOffset.FromUnixTimeSeconds(selected.ReleaseDate).Year.ToString()
                : "Date unknown";
            menu.AddButton(selected.Name, () => ShowCategories(request, selected),
                year + (selected.Verified ? " · verified" : string.Empty));
        }
        if (matches.Count == 0) menu.AddLabel("No matches were returned. Edit the title and try again.");
        menu.AddButton("Edit search", () => Open(request));
        menu.AddButton("Back", () => Open(request));
        menu.Closed = () => Open(request);
        menu.Show();
    }

    private static void ShowCategories(SteamGridArtworkRequest request, GameMatch game)
    {
        ModMenu menu = ModsUi.CreateMenu(SteamGridBoxMod.OwnerId + ".art.categories." + StableId(request),
            "Choose Artwork Shape", game.Name);
        menu.Eyebrow = "STEAMGRIDBOX";
        menu.CloseText = "BACK";
        menu.AddButton("Portrait covers", () => StartImages(request, game, SteamGridArtworkKind.Portrait), "Tall grids such as 600 × 900");
        menu.AddButton("Square covers", () => StartImages(request, game, SteamGridArtworkKind.Square), "Square grids grouped by actual dimensions");
        menu.AddButton("Wide covers", () => StartImages(request, game, SteamGridArtworkKind.Wide), "Horizontal grids such as 460 × 215 and 920 × 430");
        menu.AddButton("Hero banners", () => StartImages(request, game, SteamGridArtworkKind.Hero), "Large cinematic banners");
        if (request.IncludeLogos)
            menu.AddButton("Transparent logos", () => StartImages(request, game, SteamGridArtworkKind.Logo), "PNG logos suitable for box spines");
        menu.AddButton("Back", () => Open(request));
        menu.Closed = () => Open(request);
        menu.Show();
    }

    private static void StartImages(SteamGridArtworkRequest request, GameMatch game, SteamGridArtworkKind kind)
    {
        if (busy) return;
        busy = true;
        int operation = ++operationSerial;
        ShowLoading(request, "Loading " + KindTitle(kind), game.Name, () => ShowCategories(request, game));
        MelonCoroutines.Start(ImagesRoutine(request, game, kind, operation));
    }

    private static IEnumerator ImagesRoutine(SteamGridArtworkRequest request, GameMatch game,
        SteamGridArtworkKind kind, int operation)
    {
        string endpoint = kind switch
        {
            SteamGridArtworkKind.Hero => "heroes",
            SteamGridArtworkKind.Logo => "logos",
            _ => "grids"
        };
        string mimes = kind == SteamGridArtworkKind.Logo ? "image%2Fpng" : "image%2Fpng,image%2Fjpeg";
        string url = ApiRoot + "/" + endpoint + "/game/" + game.Id +
            "?types=static&nsfw=false&humor=false&mimes=" + mimes;
        JArray data = null;
        string error = null;
        yield return GetData(url, (value, message) => { data = value; error = message; });
        if (operation != operationSerial) yield break;
        if (data == null)
        {
            busy = false;
            ShowCategories(request, game);
            ShowError(error);
            yield break;
        }

        List<ArtworkChoice> images = data.Select(ToChoice).Where(value => Matches(kind, value))
            .OrderByDescending(value => value.Score)
            .ThenByDescending(value => value.Width * value.Height).ToList();
        if (images.Count == 0)
        {
            busy = false;
            ShowCategories(request, game);
            ModsUi.ShowToast("No " + KindTitle(kind).ToLowerInvariant() + " were returned for " + game.Name + ".", 8f);
            yield break;
        }
        yield return PreviewRoutine(request, game, kind, images, 0, operation);
    }

    private static void StartPreview(SteamGridArtworkRequest request, GameMatch game,
        SteamGridArtworkKind kind, IReadOnlyList<ArtworkChoice> images, int index)
    {
        if (busy) return;
        busy = true;
        int operation = ++operationSerial;
        ShowLoading(request, "Loading preview", game.Name + " · " + (index + 1) + " of " + images.Count,
            () => ShowCategories(request, game));
        MelonCoroutines.Start(PreviewRoutine(request, game, kind, images, index, operation));
    }

    private static IEnumerator PreviewRoutine(SteamGridArtworkRequest request, GameMatch game,
        SteamGridArtworkKind kind, IReadOnlyList<ArtworkChoice> images, int index, int operation)
    {
        ArtworkChoice selected = images[Math.Clamp(index, 0, images.Count - 1)];
        byte[] bytes = null;
        string error = null;
        // Keep the full-resolution bytes that will actually be applied. Using the
        // thumbnail here would silently downgrade the selected artwork.
        yield return DownloadBytes(selected.Url,
            (value, message) => { bytes = value; error = message; }, 20 * 1024 * 1024);
        if (operation != operationSerial) yield break;
        if (bytes == null)
        {
            busy = false;
            ShowCategories(request, game);
            ShowError(error);
            yield break;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, bytes, false))
        {
            UnityEngine.Object.Destroy(texture);
            busy = false;
            ShowCategories(request, game);
            ShowError("SteamGridDB returned artwork that Unity could not decode.");
            yield break;
        }
        if (previewTexture != null) UnityEngine.Object.Destroy(previewTexture);
        previewTexture = texture;
        busy = false;

        ModMenu menu = ModsUi.CreateMenu(SteamGridBoxMod.OwnerId + ".art.preview." + StableId(request),
            KindTitle(kind), game.Name + " · " + (index + 1) + " of " + images.Count);
        menu.Eyebrow = "STEAMGRIDBOX";
        menu.CloseText = "BACK";
        menu.AddImage(() => previewTexture, 420f);
        menu.AddLabel(selected.Width + " × " + selected.Height +
            (string.IsNullOrWhiteSpace(selected.Style) ? string.Empty : " · " + selected.Style) +
            (string.IsNullOrWhiteSpace(selected.Author) ? string.Empty : " · by " + selected.Author));
        if (index + 1 < images.Count)
            menu.AddButton("Next", () => StartPreview(request, game, kind, images, index + 1));
        if (index > 0)
            menu.AddButton("Previous", () => StartPreview(request, game, kind, images, index - 1));
        menu.AddButton(kind == SteamGridArtworkKind.Logo ? "Use as transparent logo" : "Use this artwork", () =>
        {
            request.Apply(bytes, kind);
            ShowCategories(request, game);
        });
        menu.AddButton("Back to shapes", () => ShowCategories(request, game));
        menu.Closed = () => ShowCategories(request, game);
        menu.Show();
    }

    private static ArtworkChoice ToChoice(JToken token)
    {
        if (token == null) return null;
        JToken style = token["style"];
        string styleText = style?.Type == JTokenType.Array
            ? string.Join(", ", style.Values<string>())
            : (string)style ?? string.Empty;
        return new ArtworkChoice
        {
            Url = (string)token["url"] ?? string.Empty,
            Thumb = (string)token["thumb"] ?? string.Empty,
            Width = (int?)token["width"] ?? 0,
            Height = (int?)token["height"] ?? 0,
            Score = (int?)token["score"] ?? 0,
            Style = styleText,
            Author = (string)token["author"]?["name"] ?? string.Empty
        };
    }

    private static bool Matches(SteamGridArtworkKind kind, ArtworkChoice image)
    {
        if (image == null || image.Width <= 0 || image.Height <= 0 || string.IsNullOrWhiteSpace(image.Url)) return false;
        if (kind is SteamGridArtworkKind.Hero or SteamGridArtworkKind.Logo) return true;
        float aspect = image.Width / (float)image.Height;
        return kind switch
        {
            SteamGridArtworkKind.Portrait => aspect < 0.9f,
            SteamGridArtworkKind.Square => aspect >= 0.9f && aspect <= 1.1f,
            SteamGridArtworkKind.Wide => aspect > 1.1f,
            _ => false
        };
    }

    private static string KindTitle(SteamGridArtworkKind kind) => kind switch
    {
        SteamGridArtworkKind.Portrait => "Portrait Covers",
        SteamGridArtworkKind.Square => "Square Covers",
        SteamGridArtworkKind.Wide => "Wide Covers",
        SteamGridArtworkKind.Hero => "Hero Banners",
        _ => "Transparent Logos"
    };

    private static void ShowLoading(SteamGridArtworkRequest request, string title, string subtitle, Action cancelled)
    {
        int loadingOperation = operationSerial;
        ModMenu menu = ModsUi.CreateMenu(SteamGridBoxMod.OwnerId + ".art.loading." + StableId(request), title, subtitle);
        menu.Eyebrow = "STEAMGRIDBOX";
        menu.CloseText = "CANCEL";
        menu.AddLabel("Please wait…");
        menu.AddButton("Cancel", () =>
        {
            operationSerial++;
            busy = false;
            cancelled?.Invoke();
        });
        menu.Closed = () =>
        {
            if (operationSerial != loadingOperation) return;
            operationSerial++;
            busy = false;
            cancelled?.Invoke();
        };
        menu.Show();
    }

    private static IEnumerator GetData(string url, Action<JArray, string> completed)
    {
        while (true)
        {
            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Authorization", "Bearer " + SteamGridBoxMod.Instance.ApiKey);
            request.timeout = 30;
            yield return request.SendWebRequest();
            if (request.responseCode == 429)
            {
                float delay = RetryDelay(request);
                ModsUi.ShowToast("SteamGridDB rate limit reached. Retrying in " + Mathf.CeilToInt(delay) + " seconds…",
                    Math.Min(10f, delay));
                yield return new WaitForSecondsRealtime(delay);
                continue;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                completed(null, FriendlyError(request));
                yield break;
            }
            try { completed(JObject.Parse(request.downloadHandler.text)["data"] as JArray ?? new JArray(), null); }
            catch (Exception ex) { completed(null, "SteamGridDB returned an unreadable response: " + ex.Message); }
            yield break;
        }
    }

    private static IEnumerator DownloadBytes(string url, Action<byte[], string> completed, int limit)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            completed(null, "SteamGridDB returned an invalid artwork URL.");
            yield break;
        }
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = 30;
        yield return request.SendWebRequest();
        byte[] bytes = request.downloadHandler?.data;
        if (request.result != UnityWebRequest.Result.Success) completed(null, FriendlyError(request));
        else if (bytes == null || bytes.Length < 128) completed(null, "SteamGridDB returned an empty artwork file.");
        else if (bytes.Length > limit) completed(null, "SteamGridDB artwork exceeds the download limit.");
        else completed(bytes, null);
    }

    private static float RetryDelay(UnityWebRequest request)
    {
        string value = request.GetResponseHeader("Retry-After");
        if (float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float seconds))
            return Mathf.Clamp(seconds, 1f, 900f);
        return 60f;
    }

    private static string FriendlyError(UnityWebRequest request)
    {
        string detail = null;
        try
        {
            JObject parsed = JObject.Parse(request.downloadHandler?.text ?? string.Empty);
            detail = string.Join(", ", parsed["errors"]?.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Take(3)
                ?? Enumerable.Empty<string>());
        }
        catch { }
        string status = request.responseCode > 0
            ? "SteamGridDB request failed: " + request.responseCode + " " + (request.error ?? "request failed")
            : request.error ?? "SteamGridDB request failed.";
        return string.IsNullOrWhiteSpace(detail) ? status : status + ": " + detail;
    }

    private static void ShowError(string error)
    {
        string message = string.IsNullOrWhiteSpace(error) ? "SteamGridDB operation failed." : error;
        Melon<SteamGridBoxMod>.Logger.Warning(message);
        ModsUi.ShowToast(message, 10f);
    }

    private static string StableId(SteamGridArtworkRequest request) =>
        string.IsNullOrWhiteSpace(request.Id) ? "shared" : request.Id.Replace(" ", "-");
}
