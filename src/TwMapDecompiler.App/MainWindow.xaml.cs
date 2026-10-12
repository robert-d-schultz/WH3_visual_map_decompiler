using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using TwMapDecompiler.Core.Bmd;
using TwMapDecompiler.Core.Pipeline;
using TwMapDecompiler.Core.Tiles;

namespace TwMapDecompiler.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private Decompiler? _lastRun;
    private readonly Dictionary<StepId, CheckBox> _stepBoxes = [];
    private IReadOnlyList<StepInfo> _stepInfos = [];
    private bool _loaded;
    private List<CampaignEntry> _campaigns = [];

    /// <summary>
    /// Sub-option of the Height map step, shown indented under it in the steps list. It is
    /// built once and re-added on every refresh, so its state survives the list rebuild.
    /// </summary>
    internal readonly CheckBox RefineHeightBox = new()
    {
        IsChecked = false,
        Margin = new Thickness(20, 0, 0, 6),
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Refine the heights with the normal map" },
                new TextBlock
                {
                    Text = "A minute or two; only helps where lf_normal.dds was made from the same heights",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                },
            },
        },
        ToolTip = "Moves each land height within its full_logic_map.compressed_map step to fit lf_normal.dds. " +
                  "The heights still recompile to the same logic map.",
    };

    /// <summary>Sub-option of the Prop layers step, handled like <see cref="RefineHeightBox"/>.</summary>
    internal readonly CheckBox AddCustomCulturesBox = new()
    {
        IsChecked = true,
        Margin = new Thickness(20, 0, 0, 6),
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Add modded cultures to culture masks" },
                new TextBlock
                {
                    Text = string.Join(", ", CultureMask.CustomCultures.Select(c => c.Custom)),
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        },
        ToolTip = "Hardcoded: a culture mask naming all of a modded culture's vanilla cultures also names it.\n" +
                  string.Join("\n", CultureMask.CustomCultures.Select(c => $"{c.Custom} follows {string.Join(" + ", c.Vanilla)}")),
    };

    public MainWindow()
    {
        InitializeComponent();
        AppSettings.Load(this);
        _loaded = true;
        RefreshSteps();
        UpdateOutputNote();
        UpdateResizePreview();
        _ = LoadCampaignsAsync();
    }

    // ------------------------------------------------------------ campaigns

    /// <summary>Selected campaign row, or null while the list is still loading.</summary>
    private CampaignEntry? SelectedCampaign =>
        _campaigns.FirstOrDefault(c => string.Equals(c.CampaignName, CampaignName, StringComparison.OrdinalIgnoreCase));

    private string CampaignName =>
        (CampaignCombo.SelectedItem as CampaignEntry)?.CampaignName ?? (CampaignCombo.Text ?? "").Trim();

    /// <summary>Reads the campaigns table from the packs and fills the drop-down.</summary>
    private async Task LoadCampaignsAsync()
    {
        string wanted = CampaignName;
        ReloadCampaignsButton.IsEnabled = false;
        CampaignCombo.IsEnabled = false;
        ShowStatus("Loading campaigns from the game packs...");
        try
        {
            string game = GameCombo.SelectedIndex == 1 ? "three_kingdoms" : "warhammer_3";
            string? rpfm = OrNull(RpfmFolderBox.Text);
            _campaigns = await Task.Run(() => CampaignCatalog.LoadAsync(rpfm, game, _ => { }));

            CampaignCombo.ItemsSource = _campaigns;
            CampaignCombo.SelectedItem = _campaigns.FirstOrDefault(c =>
                string.Equals(c.CampaignName, wanted, StringComparison.OrdinalIgnoreCase)) ?? _campaigns.FirstOrDefault();
            if (CampaignCombo.SelectedItem == null && wanted.Length > 0)
                CampaignCombo.Text = wanted;
            ShowCampaignDetails();
            UpdateResizePreview();
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not read the campaigns table: {ex.Message}\nCheck the RPFM folder, " +
                       "or type a campaign name and fill in the overrides below.");
        }
        finally
        {
            ReloadCampaignsButton.IsEnabled = true;
            CampaignCombo.IsEnabled = true;
        }
    }

    /// <summary>Shows a message in place of the resolved-paths table.</summary>
    private void ShowStatus(string message)
    {
        CampaignStatus.Text = message;
        CampaignStatus.Visibility = Visibility.Visible;
        CampaignDetails.Visibility = Visibility.Collapsed;
    }

    /// <summary>Lists what the selected campaign resolves to, so it is clear before running.</summary>
    private void ShowCampaignDetails()
    {
        if (SelectedCampaign is not { } c)
        {
            ShowStatus(_campaigns.Count == 0
                ? "No campaigns loaded."
                : "Unknown campaign - the overrides below decide what is read.");
            return;
        }

        DetailTerrain.Text = c.TerrainLocationDevastate != null
            ? $"terrain/campaigns/{c.TerrainLocation}/ (devastated: terrain/campaigns/{c.TerrainLocationDevastate}/)"
            : $"terrain/campaigns/{c.TerrainLocation}/";
        DetailTrees.Text = $"campaign_maps/{c.DisplayLocation}/display/trees/";
        DetailBattles.Text = $"terrain/battles/{c.BattlePath}/";
        DetailWidth.Text = c.WorldWidth?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown";

        CampaignStatus.Visibility = Visibility.Collapsed;
        CampaignDetails.Visibility = Visibility.Visible;
    }

    private void OnCampaignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        ShowCampaignDetails();
        RefreshSteps();
        UpdateResizePreview();
    }

    // ------------------------------------------------------------ resize

    /// <summary>
    /// The hex grid the scale and padding are counted on: the local terrain folder's tile_list.bin when
    /// overridden, otherwise the selected campaign's. Null when it is not known (yet).
    /// </summary>
    private HexGrid? SourceHexGrid
    {
        get
        {
            if (OrNull(InputFolderBox.Text) is { } folder)
            {
                string tileList = Path.Combine(folder, "tile_list.bin");
                return File.Exists(tileList) ? CampaignCatalog.ReadHexGrid(tileList) : null;
            }
            return SelectedCampaign?.Hexes;
        }
    }

    /// <summary>Reads the scale factor and the padding; false with a message when one is not a valid number.</summary>
    private bool TryReadResize(out double scale, out MapPadding padding, out string? error)
    {
        scale = 1;
        padding = MapPadding.None;
        error = null;
        if (OrNull(ScaleBox.Text) is { } scaleText &&
            (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || !(scale > 0) || double.IsInfinity(scale)))
        {
            error = "The map scale must be a number above 0 (empty = 1).";
            return false;
        }

        var sides = new int[4];
        var padBoxes = new[] { (PadLeftBox, "left"), (PadRightBox, "right"), (PadTopBox, "top"), (PadBottomBox, "bottom") };
        for (int i = 0; i < padBoxes.Length; i++)
        {
            var (box, side) = padBoxes[i];
            if (OrNull(box.Text) is { } text && (!int.TryParse(text, out sides[i]) || sides[i] < 0))
            {
                error = $"Map padding ({side}) must be a whole number of hexes, 0 or more.";
                return false;
            }
        }
        padding = new MapPadding(sides[0], sides[1], sides[2], sides[3]);
        return true;
    }

    /// <summary>The run's resize, when the hex grid is known; with the reason it cannot be run, if any.</summary>
    private MapResizer? PlanResize(out string? error)
    {
        if (!TryReadResize(out double scale, out var padding, out error))
            return null;
        if (SourceHexGrid is not { } source)
            return null;
        var resizer = new MapResizer(MapScale.Plan(source, scale), padding);
        if (resizer.Final.Problem is { } problem)
            error = $"The hex map would be {resizer.Final}, and {problem}." +
                    (resizer.Final.Width % 2 != 0 ? " Pad left + right by an even number of hexes." : "");
        return resizer;
    }

    private void OnResizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded)
            UpdateResizePreview();
    }

    /// <summary>Shows what the scale snaps to, and the final hex map once padded.</summary>
    private void UpdateResizePreview()
    {
        var source = SourceHexGrid;
        ResizeSource.Text = source is { } grid
            ? $"Counted on the campaign hex map, {grid} hexes (tile_list.bin). Terry takes an even number of hexes across and at most {HexGrid.MaxSize} each way."
            : "The campaign hex map's size is not known until the campaigns load (or, with a local terrain folder, from its tile_list.bin); " +
              "the run reads it either way.";

        var resizer = PlanResize(out string? error);
        bool readable = TryReadResize(out double requested, out var padding, out _);
        bool asked = readable && !(requested == 1 && padding.IsNone);
        ScaleNote.Text = "";
        ResizeResult.Foreground = error != null ? Brushes.IndianRed : SystemColors.ControlTextBrush;

        if (resizer == null)
        {
            ScaleResult.Text = readable && requested != 1 ? $"scale {requested:0.######}, snapped to the hex map at run time" : "";
            ResizeResult.Text = error ?? (asked ? "The final hex map is worked out at run time." : "The map keeps its size.");
            ResizeHeader.Text = asked ? "Resize the map (on)" : error != null ? "Resize the map (invalid)" : "Resize the map";
            return;
        }

        var scale = resizer.Scale;
        if (resizer.IsScaled)
        {
            ScaleResult.Text = $"→ hex map {scale.Scaled}, scale {scale.Effective:0.######}" +
                               (Math.Abs(scale.Effective - scale.Requested) > 1e-9 ? $" (nearest to {scale.Requested:0.######})" : "");
            string residual = Math.Abs(scale.NorthResidual) < 0.0005 ? ""
                : $"The scaled map is {Math.Abs(scale.NorthResidual):0.###} hexes {(scale.NorthResidual > 0 ? "short of" : "past")} the hex map's north edge, " +
                  $"where {(scale.NorthResidual > 0 ? "the map's edge is repeated" : "it is cropped")}: the half hex the staggered columns add does not scale with the rows.";
            ScaleNote.Text = (scale.Capped ? $"Capped: no larger scale fits {HexGrid.MaxSize} hexes. " : "") + residual;
        }
        else
        {
            ScaleResult.Text = readable && requested != 1 ? $"→ hex map {scale.Scaled}: too small a change to move it by a hex" : "";
        }

        var final = resizer.Final;
        var (tileWidth, tileHeight) = resizer.Size(2 * scale.Source.Width, 2 * scale.Source.Height + 1);
        var (heightWidth, heightHeight) = resizer.Size(8 * scale.Source.Width, 8 * scale.Source.Height + 4);
        double? worldWidth = OrNull(InputFolderBox.Text) == null ? SelectedCampaign?.WorldWidth : null;
        ResizeResult.Text = error ?? (resizer.IsNone
            ? "The map keeps its size."
            : $"Final hex map {final}: tile map {tileWidth}x{tileHeight}, height map {heightWidth}x{heightHeight}" +
              (worldWidth is { } w ? $", world width {resizer.WorldWidth(w):0.##} (from {w:0.##})" : ""));
        ResizeHeader.Text = resizer.IsNone ? "Resize the map" : $"Resize the map: {scale.Source} → {final} hexes" + (error != null ? " (invalid)" : "");
    }

    private async void ReloadCampaigns(object sender, RoutedEventArgs e) => await LoadCampaignsAsync();


    private async void OnGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
            await LoadCampaignsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        AppSettings.Save(this);
        base.OnClosed(e);
    }

    // ------------------------------------------------------------ settings

    private DecompileSettings? BuildSettings(out string? error)
    {
        error = null;
        string campaign = CampaignName;
        if (campaign.Length == 0)
        {
            error = "Select a campaign.";
            return null;
        }

        if (!TryReadResize(out double scale, out var padding, out error))
            return null;
        PlanResize(out error);
        if (error != null)
            return null;

        return new DecompileSettings
        {
            VanillaCampaignName = campaign,
            Scale = scale,
            Padding = padding,
            MapName = OrNull(MapNameBox.Text),
            CampaignName = OrNull(CampaignNameBox.Text),
            BattleMapName = OrNull(BattleMapNameBox.Text),
            RpfmFolder = OrNull(RpfmFolderBox.Text),
            InputFolder = OrNull(InputFolderBox.Text),
            TreeListPath = OrNull(TreeListBox.Text),
            TileDatabaseFolder = OrNull(TileDbBox.Text),
            BattlesFolder = OrNull(BattlesBox.Text),
            BlendTexturesPath = OrNull(BlendTexturesBox.Text),
            AssemblyKitFolder = OrNull(AssemblyKitBox.Text),
            Game = GameCombo.SelectedIndex == 1 ? TileGame.ThreeKingdoms : TileGame.Warhammer3,
            RefineHeightWithNormals = RefineHeightBox.IsChecked == true,
            AddCustomCultures = AddCustomCulturesBox.IsChecked == true,
            DevastatedMapName = OrNull(DevastatedNameBox.Text),
        };
    }

    private static string? OrNull(string text) => text.Trim().Length > 0 ? text.Trim() : null;

    // ------------------------------------------------------------ steps list

    private void RefreshSteps()
    {
        if (!_loaded)
            return;

        var previouslyChecked = _stepBoxes
            .Where(kv => kv.Value.IsChecked == true)
            .Select(kv => kv.Key)
            .ToHashSet();
        bool hadBoxes = _stepBoxes.Count > 0;

        // Availability only depends on the local overrides, so show the list before a map is named.
        var settings = BuildSettings(out _) ?? new DecompileSettings
        {
            VanillaCampaignName = "",
            InputFolder = OrNull(InputFolderBox.Text),
            TreeListPath = OrNull(TreeListBox.Text),
            TileDatabaseFolder = OrNull(TileDbBox.Text),
            BattlesFolder = OrNull(BattlesBox.Text),
            BlendTexturesPath = OrNull(BlendTexturesBox.Text),
        };
        _stepInfos = Decompiler.DescribeSteps(settings);

        StepsList.Items.Clear();
        _stepBoxes.Clear();

        foreach (var info in _stepInfos)
        {
            var box = new CheckBox
            {
                Margin = new Thickness(0, 3, 0, 3),
                IsEnabled = info.Available,
                IsChecked = info.Available && (hadBoxes ? previouslyChecked.Contains(info.Id) : info.DefaultEnabled),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold },
                        new TextBlock
                        {
                            Text = info.Available ? info.Description : info.UnavailableReason,
                            FontSize = 11,
                            Foreground = info.Available ? Brushes.Gray : Brushes.IndianRed,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            _stepBoxes[info.Id] = box;
            StepsList.Items.Add(box);

            // Sub-options belong to their step: indented under it, and only usable while that
            // step is on.
            if (info.Id == StepId.Height)
            {
                BindingOperations.SetBinding(RefineHeightBox, IsEnabledProperty,
                    new Binding(nameof(CheckBox.IsChecked)) { Source = box });
                StepsList.Items.Add(RefineHeightBox);
            }
            else if (info.Id == StepId.Layers)
            {
                BindingOperations.SetBinding(AddCustomCulturesBox, IsEnabledProperty,
                    new Binding(nameof(CheckBox.IsChecked)) { Source = box });
                StepsList.Items.Add(AddCustomCulturesBox);
            }
        }
    }

    private void SelectAllSteps(object sender, RoutedEventArgs e)
    {
        foreach (var box in _stepBoxes.Values.Where(b => b.IsEnabled))
            box.IsChecked = true;
    }

    private void SelectNoSteps(object sender, RoutedEventArgs e)
    {
        foreach (var box in _stepBoxes.Values)
            box.IsChecked = false;
    }

    // ------------------------------------------------------------ path pickers

    private void OnOverrideChanged(object sender, TextChangedEventArgs e)
    {
        RefreshSteps();
        if (_loaded && sender == InputFolderBox)
            UpdateResizePreview();
    }

    private void OnOutputPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded)
            UpdateOutputNote();
    }


    /// <summary>Says where the run will actually write, since the default comes from RPFM.</summary>
    private void UpdateOutputNote()
    {
        OutputNote.Text = OrNull(AssemblyKitBox.Text) is { } ak
            ? $@"Both Terry projects go under {ak}\raw_data\terrain, the lighting environment under working_data\Weather."
            : "Both Terry projects and the lighting environment go into the Assembly Kit RPFM has configured.";
    }

    private void BrowseRpfmFolder(object sender, RoutedEventArgs e) => BrowseFolder(RpfmFolderBox);
    private void BrowseInputFolder(object sender, RoutedEventArgs e) => BrowseFolder(InputFolderBox);
    private void BrowseTileDb(object sender, RoutedEventArgs e) => BrowseFolder(TileDbBox);
    private void BrowseBattles(object sender, RoutedEventArgs e) => BrowseFolder(BattlesBox);
    private void BrowseAssemblyKit(object sender, RoutedEventArgs e) => BrowseFolder(AssemblyKitBox);

    private void BrowseTreeList(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Campaign tree list|*.campaign_tree_list|All files|*.*",
            FileName = TreeListBox.Text,
        };
        if (dialog.ShowDialog(this) == true)
            TreeListBox.Text = dialog.FileName;
    }

    private void BrowseBlendTextures(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Asset variation database|*.assetdb.xml|All files|*.*",
            FileName = BlendTexturesBox.Text,
        };
        if (dialog.ShowDialog(this) == true)
            BlendTexturesBox.Text = dialog.FileName;
    }

    private void BrowseFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = target.Text };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
    }

    // ------------------------------------------------------------ run

    private async void RunClicked(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings(out string? error);
        if (settings == null)
        {
            MessageBox.Show(this, error, "Total War Map Decompiler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var steps = _stepInfos
            .Where(i => _stepBoxes.TryGetValue(i.Id, out var box) && box.IsChecked == true)
            .Select(i => i.Id)
            .ToList();
        if (steps.Count == 0)
        {
            MessageBox.Show(this, "Select at least one step.", "Total War Map Decompiler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetRunning(true);
        LogBox.Clear();
        Progress.Value = 0;
        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;

        int totalSteps = steps.Count;
        int stepIndex = -1;
        StepId? currentStep = null;

        void Log(string message) => Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        void Report(StepId step, double fraction) => Dispatcher.InvokeAsync(() =>
        {
            if (currentStep != step)
            {
                currentStep = step;
                stepIndex++;
            }
            Progress.Value = Math.Min(100, (stepIndex + fraction) / totalSteps * 100);
            ProgressText.Text = $"{step} ({stepIndex + 1}/{totalSteps})";
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _lastRun = new Decompiler(settings);
            await Task.Run(() => _lastRun.Run(steps, Log, Report, ct), ct);
            Log($"=== Finished in {stopwatch.Elapsed.TotalSeconds:0.0}s ===");
            Progress.Value = 100;
            ProgressText.Text = "Done";
        }
        catch (OperationCanceledException)
        {
            Log("=== Cancelled ===");
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            Log($"=== FAILED: {ex.Message} ===");
            Log(ex.ToString());
            ProgressText.Text = "Failed";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetRunning(false);
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        foreach (var control in new Control[] { CampaignCombo, ReloadCampaignsButton, RpfmFolderBox, GameCombo,
                     MapNameBox, CampaignNameBox, BattleMapNameBox, DevastatedNameBox,
                     InputFolderBox, TreeListBox, TileDbBox, BattlesBox, BlendTexturesBox, AssemblyKitBox,
                     ScaleBox, PadLeftBox, PadRightBox, PadTopBox, PadBottomBox })
            control.IsEnabled = !running;
        foreach (var box in _stepBoxes.Values)
            box.IsEnabled = !running && _stepInfos.First(i => i.Id == _stepBoxes.First(kv => kv.Value == box).Key).Available;
        if (!running && SelectedCampaign != null)
            ShowCampaignDetails(); // re-applies whether the campaign has a devastated map
    }

    /// <summary>Opens both project folders, as the last run resolved them.</summary>
    private void OpenOutput(object sender, RoutedEventArgs e)
    {
        var folders = new[] { _lastRun?.OutputFolder, _lastRun?.BattleOutputFolder };
        bool opened = false;
        foreach (string? folder in folders)
        {
            if (folder != null && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                opened = true;
            }
        }
        if (!opened)
            MessageBox.Show(this, "Run the decompiler first - the project folders are resolved from the Assembly Kit at run time.",
                "Total War Map Decompiler", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
