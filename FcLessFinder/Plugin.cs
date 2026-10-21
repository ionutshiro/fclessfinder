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

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTime.Now - lastScan < TimeSpan.FromSeconds(1))
            return;
        lastScan = DateTime.Now;

        ScanPlayerSearchResults();
        if (Config.CollectNearby)
            ScanNearbyPlayers();
    }

    /// <summary>Reads whatever the game's own Social > Player Search window currently holds.</summary>
    private void ScanPlayerSearchResults()
    {
        var proxy = InfoProxySearch.Instance();
        if (proxy == null)
            return;

        var count = proxy->GetEntryCount();
        for (uint i = 0; i < count; i++)
        {
            var entry = proxy->GetEntry(i);
            if (entry == null || entry->ContentId == 0)
                continue;
            Observe(entry->NameString, entry->HomeWorld, entry->FCTagString, entry->Job, "Player Search");
        }
    }

    private void ScanNearbyPlayers()
    {
        var self = ObjectTable.LocalPlayer;
        foreach (var pc in ObjectTable.PlayerObjects.OfType<IPlayerCharacter>())
        {
            if (self != null && pc.Address == self.Address)
                continue;
            Observe(pc.Name.TextValue, (ushort)pc.HomeWorld.RowId, pc.CompanyTag.TextValue, (byte)pc.ClassJob.RowId, "Nearby");
        }
    }

    private void Observe(string name, ushort homeWorldId, string fcTag, byte jobId, string source)
    {
        if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0)
            return;

        var world = DataManager.GetExcelSheet<World>().GetRowOrDefault(homeWorldId)?.Name.ExtractText() ?? "";
        if (world.Length == 0)
            return;

        var key = $"{name}@{world}";
        if (!string.IsNullOrWhiteSpace(fcTag))
        {
            // They have (or just joined) an FC: drop them from the list.
            Roster.Remove(key);
            return;
        }

        if (!Roster.TryGetValue(key, out var p))
        {
            p = new PlayerEntry { Name = name, HomeWorldId = homeWorldId, HomeWorld = world };
            Roster[key] = p;
        }

        p.Job = DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId)?.Abbreviation.ExtractText() ?? "";
        p.Source = source;
        p.LastSeen = DateTime.Now;
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
