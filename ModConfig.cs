using System.IO;
using MelonLoader.Utils;
using Newtonsoft.Json;

namespace SteamGridBox;

internal sealed class ModConfig
{
    public string ApiKey { get; set; } = "";
    public bool SkipExistingCoverArt { get; set; } = true;

    [JsonIgnore]
    internal static string FilePath => Path.Combine(MelonEnvironment.UserDataDirectory, "SteamGridBox", "config.json");

    internal static ModConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<ModConfig>(File.ReadAllText(FilePath)) ?? new ModConfig();
        }
        catch { }
        return new ModConfig();
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}
