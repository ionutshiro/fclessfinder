using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FcLessFinder;

public sealed class PlayerEntry
{
    public required string Name { get; init; }
    public required ushort HomeWorldId { get; init; }
    public required string HomeWorld { get; init; }
    public string Job { get; set; } = "";
    /// <summary>0 = unknown. Player Search results don't include level; only nearby players do.</summary>
    public byte Level { get; set; }
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime LastSeen { get; set; }
    public string Key => $"{Name}@{HomeWorld}";
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fcless";
    private const int MaxChatBytes = 500;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Config { get; }
    public Dictionary<string, PlayerEntry> Roster { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastTellSent { get; private set; } = DateTime.MinValue;

    private readonly WindowSystem windowSystem = new("FcLessFinder");
    private readonly MainWindow mainWindow;
    private DateTime lastScan = DateTime.MinValue;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Open the FC-less player finder.",
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
    }

    public void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    public ushort? LocalHomeWorldId => (ushort?)ObjectTable.LocalPlayer?.HomeWorld.RowId;

    /// <summary>Statuses worth showing, as OnlineStatus sheet row ids (= bit index in Player Search status flags).</summary>
    private static readonly uint[] ShownStatuses =
    [
        32, // New Adventurer
        44, // Trial Adventurer
        31, // Returner
        27, // Mentor
        28, // PvE (battle) mentor
        11, // Battle mentor
        29, // Trade mentor
        30, // PvP mentor
    ];

    /// <summary>Entries the game's Player Search currently holds, and how many of them have no FC.</summary>
    public int SearchResultCount { get; private set; }
    public int SearchWithoutFcCount { get; private set; }
    public DateTime LastSearchRequest { get; private set; } = DateTime.MinValue;
    public static readonly TimeSpan SearchRequestCooldown = TimeSpan.FromSeconds(10);

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTime.Now - lastScan < TimeSpan.FromSeconds(1))
            return;
        ScanNow();
    }

    public void ScanNow()
    {
        lastScan = DateTime.Now;
        ScanPlayerSearchResults();
        if (Config.CollectNearby)
            ScanNearbyPlayers();
    }

    /// <summary>
    /// Re-sends the last search made in the game's Player Search window (same filters), then rescans.
    /// Returns an error when no search has been made yet or when it was re-sent too recently.
    /// </summary>
    public string? RefreshSearch()
    {
        var wait = LastSearchRequest + SearchRequestCooldown - DateTime.Now;
        if (wait > TimeSpan.Zero)
            return $"Wait {wait.TotalSeconds:0}s before refreshing the search again.";

        var proxy = InfoProxySearch.Instance();
        // LevelMax is filled by the game once a search has been made; without one the filters are empty.
        if (proxy == null || proxy->LevelMax == 0)
            return "Run a search in Social > Player Search first; Refresh repeats it.";

        LastSearchRequest = DateTime.Now;
        Framework.RunOnFrameworkThread(() =>
        {
            InfoProxySearch.Instance()->RequestData();
            ScanNow();
        });
        return null;
    }

    /// <summary>Reads whatever the game's own Social > Player Search window currently holds.</summary>
    private void ScanPlayerSearchResults()
    {
        var proxy = InfoProxySearch.Instance();
        if (proxy == null)
            return;

        var count = proxy->GetEntryCount();
        var withoutFc = 0;
        for (uint i = 0; i < count; i++)
        {
            var entry = proxy->GetEntry(i);
            // ContentId is often 0 in search results, so don't use it to validate entries.
            // IsOtherServer entries carry no FC data, so an empty tag there means "unknown", not "no FC".
            if (entry == null || entry->IsOtherServer)
                continue;

            var world = entry->HomeWorld != 0 ? entry->HomeWorld : entry->CurrentWorld;
            var state = (ulong)entry->State;
            var statuses = ShownStatuses.Where(id => (state & (1ul << (int)id)) != 0);
            if (Observe(entry->NameString, world, entry->FCTagString, entry->Job, 0, statuses, "Player Search"))
                withoutFc++;
        }

        SearchResultCount = (int)count;
        SearchWithoutFcCount = withoutFc;
    }

    private void ScanNearbyPlayers()
    {
        var self = ObjectTable.LocalPlayer;
        foreach (var pc in ObjectTable.PlayerObjects.OfType<IPlayerCharacter>())
        {
            if (self != null && pc.Address == self.Address)
                continue;
            var statuses = ShownStatuses.Where(id => id == pc.OnlineStatus.RowId);
            Observe(pc.Name.TextValue, (ushort)pc.HomeWorld.RowId, pc.CompanyTag.TextValue, (byte)pc.ClassJob.RowId, pc.Level, statuses, "Nearby");
        }
    }

    /// <summary>Adds or updates a player without an FC. Returns true when the player was kept (has no FC).</summary>
    private bool Observe(string name, ushort homeWorldId, string fcTag, byte jobId, byte level, IEnumerable<uint> statusIds, string source)
    {
        if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0)
            return false;

        var world = DataManager.GetExcelSheet<World>().GetRowOrDefault(homeWorldId)?.Name.ExtractText() ?? "";
        if (world.Length == 0)
            return false;

        var key = $"{name}@{world}";
        if (!string.IsNullOrWhiteSpace(fcTag))
        {
            // They have (or just joined) an FC: drop them from the list.
            Roster.Remove(key);
            return false;
        }

        if (!Roster.TryGetValue(key, out var p))
        {
            p = new PlayerEntry { Name = name, HomeWorldId = homeWorldId, HomeWorld = world };
            Roster[key] = p;
        }

        p.Job = DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId)?.Abbreviation.ExtractText() ?? "";
        if (level > 0)
            p.Level = level;
        var statusSheet = DataManager.GetExcelSheet<OnlineStatus>();
        p.Status = string.Join(", ", statusIds
            .Select(id => statusSheet.GetRowOrDefault(id)?.Name.ExtractText() ?? "")
            .Where(s => s.Length > 0)
            .Distinct());
        p.Source = source;
        p.LastSeen = DateTime.Now;
        return true;
    }

    public TimeSpan CooldownRemaining
    {
        get
        {
            var left = LastTellSent + TimeSpan.FromSeconds(Math.Max(3, Config.CooldownSeconds)) - DateTime.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public string BuildMessage(PlayerEntry p)
    {
        var text = Config.MessageTemplate
            .Replace("{name}", p.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{first}", p.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        // Chat input is single-line; drop control characters (newlines, tabs, etc.).
        return new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    /// <summary>Sends exactly one tell. Called only from a user's click on a row.</summary>
    public bool SendTell(PlayerEntry p, out string error)
    {
        error = "";
        if (CooldownRemaining > TimeSpan.Zero)
        {
            error = $"Wait {CooldownRemaining.TotalSeconds:0}s before the next tell.";
            return false;
        }

        var message = BuildMessage(p);
        if (message.Length == 0)
        {
            error = "Message is empty.";
            return false;
        }

        var command = $"/tell {p.Name}@{p.HomeWorld} {message}";
        if (Encoding.UTF8.GetByteCount(command) > MaxChatBytes)
        {
            error = "Message is too long for the chat box.";
            return false;
        }

        LastTellSent = DateTime.Now;
        Config.Messaged.Add(p.Key);
        SaveConfig();

        Framework.RunOnFrameworkThread(() =>
        {
            var str = Utf8String.FromString(command);
            UIModule.Instance()->ProcessChatBoxEntry(str, nint.Zero, false);
            str->Dtor(true);
        });
        Log.Information($"Sent tell to {p.Key}");
        return true;
    }
}
