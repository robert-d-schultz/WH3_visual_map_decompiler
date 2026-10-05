using System.IO;
using System.Text.Json;
using TwMapDecompiler.Core.Pipeline;

namespace TwMapDecompiler.App;

/// <summary>
/// Persists the window's fields to %APPDATA%\TwMapDecompiler\settings.v2.json. (v1 stored the
/// input/output folders as primary fields; loading those into what are now override fields
/// would silently bypass the packs and the Assembly Kit defaults.)
/// </summary>
internal sealed class AppSettings
{
    public string Campaign { get; set; } = "";
    public string RpfmFolder { get; set; } = DecompileSettings.DefaultRpfmFolder ?? "";
    public string MapName { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string BattleMapName { get; set; } = "";
    public string DevastatedName { get; set; } = "";
    public string InputFolder { get; set; } = "";
    public string TreeList { get; set; } = "";
    public string TileDb { get; set; } = "";
    public string Battles { get; set; } = "";
    public string BlendTextures { get; set; } = "";
    public string AssemblyKit { get; set; } = "";
    public string Scale { get; set; } = "";
    public string PadLeft { get; set; } = "";
    public string PadRight { get; set; } = "";
    public string PadTop { get; set; } = "";
    public string PadBottom { get; set; } = "";
    public int GameIndex { get; set; }
    public bool ReconstructHeight { get; set; } = true;
    public bool AddCustomCultures { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TwMapDecompiler", "settings.v2.json");

    public static void Load(MainWindow window)
    {
        AppSettings settings = new();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? settings;
        }
        catch
        {
            // Corrupt settings are not worth failing startup over.
        }

        // The campaign list arrives asynchronously; the text is matched against it once it does.
        window.CampaignCombo.Text = settings.Campaign;
        window.RpfmFolderBox.Text = settings.RpfmFolder;
        window.MapNameBox.Text = settings.MapName;
        window.CampaignNameBox.Text = settings.CampaignName;
        window.BattleMapNameBox.Text = settings.BattleMapName;
        window.DevastatedNameBox.Text = settings.DevastatedName;
        window.InputFolderBox.Text = settings.InputFolder;
        window.TreeListBox.Text = settings.TreeList;
        window.TileDbBox.Text = settings.TileDb;
        window.BattlesBox.Text = settings.Battles;
        window.BlendTexturesBox.Text = settings.BlendTextures;
        window.AssemblyKitBox.Text = settings.AssemblyKit;
        window.ScaleBox.Text = settings.Scale;
        window.PadLeftBox.Text = settings.PadLeft;
        window.PadRightBox.Text = settings.PadRight;
        window.PadTopBox.Text = settings.PadTop;
        window.PadBottomBox.Text = settings.PadBottom;
        window.GameCombo.SelectedIndex = settings.GameIndex;
        window.ReconstructHeightBox.IsChecked = settings.ReconstructHeight;
        window.AddCustomCulturesBox.IsChecked = settings.AddCustomCultures;
    }

    public static void Save(MainWindow window)
    {
        var settings = new AppSettings
        {
            Campaign = (window.CampaignCombo.SelectedItem as CampaignEntry)?.CampaignName ?? window.CampaignCombo.Text ?? "",
            RpfmFolder = window.RpfmFolderBox.Text,
            MapName = window.MapNameBox.Text,
            CampaignName = window.CampaignNameBox.Text,
            BattleMapName = window.BattleMapNameBox.Text,
            DevastatedName = window.DevastatedNameBox.Text,
            InputFolder = window.InputFolderBox.Text,
            TreeList = window.TreeListBox.Text,
            TileDb = window.TileDbBox.Text,
            Battles = window.BattlesBox.Text,
            BlendTextures = window.BlendTexturesBox.Text,
            AssemblyKit = window.AssemblyKitBox.Text,
            Scale = window.ScaleBox.Text,
            PadLeft = window.PadLeftBox.Text,
            PadRight = window.PadRightBox.Text,
            PadTop = window.PadTopBox.Text,
            PadBottom = window.PadBottomBox.Text,
            GameIndex = window.GameCombo.SelectedIndex,
            ReconstructHeight = window.ReconstructHeightBox.IsChecked == true,
            AddCustomCultures = window.AddCustomCulturesBox.IsChecked == true,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Failing to persist settings should never crash shutdown.
        }
    }
}
