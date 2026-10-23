using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
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
    public bool IsNewOrReturning { get; set; }
    /// <summary>"Busy" or "In duty" when last seen, otherwise empty.</summary>
    public string Unavailable { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime LastSeen { get; set; }
    [JsonIgnore] public string Key => $"{Name}@{HomeWorld}";
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
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Config { get; }
    public Dictionary<string, PlayerEntry> Roster { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastTellSent { get; private set; } = DateTime.MinValue;

    private readonly WindowSystem windowSystem = new("FcLessFinder");
    private readonly MainWindow mainWindow;
    private DateTime lastScan = DateTime.MinValue;
    private readonly HashSet<string> trialPlayers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastRosterSave = DateTime.Now;
    private string RosterPath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "roster.json");

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Migrate();
        LoadRoster();
        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Open the FC-less player finder.",
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        Framework.Update += OnFrameworkUpdate;
        ChatGui.ChatMessage += OnChatMessage;
        ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        ContextMenu.OnMenuOpened -= OnMenuOpened;
        ChatGui.ChatMessage -= OnChatMessage;
        SaveRoster();
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
    }

    public void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    /// <summary>The collected list is kept in its own file next to the config, so it survives restarts.</summary>
    private void LoadRoster()
    {
        try
        {
            if (!File.Exists(RosterPath))
                return;
            foreach (var p in JsonSerializer.Deserialize<List<PlayerEntry>>(File.ReadAllText(RosterPath)) ?? [])
                Roster[p.Key] = p;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Couldn't load the saved player list; starting empty.");
        }
    }

    public void ClearRoster()
    {
        Roster.Clear();
        SaveRoster();
    }

    private void SaveRoster()
    {
        try
        {
            Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
            // Write to a temp file first so a crash mid-write can't corrupt the saved list.
            var tmp = RosterPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Roster.Values.ToList()));
            File.Move(tmp, RosterPath, true);
            lastRosterSave = DateTime.Now;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Couldn't save the player list.");
        }
    }

    public ushort? LocalHomeWorldId => (ushort?)ObjectTable.LocalPlayer?.HomeWorld.RowId;

    /// <summary>Statuses worth showing, as OnlineStatus sheet row ids (= bit index in Player Search status flags).</summary>
    private static readonly uint[] ShownStatuses =
    [
        32, // New Adventurer
        31, // Returner
        27, // Mentor
        28, // PvE (battle) mentor
        11, // Battle mentor
        29, // Trade mentor
        30, // PvP mentor
    ];

    private static readonly uint[] NewOrReturningStatuses = [32, 31];
    /// <summary>Free trial accounts can't join a Free Company, so they're never listed.</summary>
    private const uint TrialAdventurerStatus = 44;
    private const uint BusyStatus = 12;
    private const uint InDutyStatus = 43;

    /// <summary>Entries the game's Player Search currently holds, and how many of them have no FC.</summary>
    public int SearchResultCount { get; private set; }
    public int SearchWithoutFcCount { get; private set; }
    public DateTime LastSearchRequest { get; private set; } = DateTime.MinValue;
    public static readonly TimeSpan SearchRequestCooldown = TimeSpan.FromSeconds(10);

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTime.Now - lastRosterSave > TimeSpan.FromMinutes(2))
            SaveRoster();
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
            if (Observe(entry->NameString, world, entry->FCTagString, entry->Job, 0, id => (state & (1ul << (int)id)) != 0, "Player Search"))
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
            // Nearby characters carry a single online status (the icon above their head).
            var status = pc.OnlineStatus.RowId;
            Observe(pc.Name.TextValue, (ushort)pc.HomeWorld.RowId, pc.CompanyTag.TextValue, (byte)pc.ClassJob.RowId, pc.Level, id => id == status, "Nearby");
        }
    }

    /// <summary>Adds or updates a player without an FC. Returns true when the player was kept (has no FC).</summary>
    private bool Observe(string name, ushort homeWorldId, string fcTag, byte jobId, byte level, Func<uint, bool> hasStatus, string source)
    {
        if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0)
            return false;

        var world = DataManager.GetExcelSheet<World>().GetRowOrDefault(homeWorldId)?.Name.ExtractText() ?? "";
        if (world.Length == 0)
            return false;

        var key = $"{name}@{world}";
        // Nearby characters show one status icon, so remember trial players once identified.
        if (hasStatus(TrialAdventurerStatus))
            trialPlayers.Add(key);
        if (!string.IsNullOrWhiteSpace(fcTag) && Config.Messaged.Contains(key)
            && MyFcTag is { Length: > 0 } mine && NormalizeTag(fcTag) == mine
            && Config.Progress.GetValueOrDefault(key) != Joined)
        {
            // Someone we recruited now wears our FC tag.
            SetProgress(key, Joined);
            ChatGui.Print($"[FC-less Finder] {key} joined your Free Company.");
        }

        if (!string.IsNullOrWhiteSpace(fcTag) || trialPlayers.Contains(key))
        {
            // They have (or just joined) an FC, or are on a free trial: drop them from the list.
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
        // Player Search reports every status; a nearby character shows only one icon,
        // so a nearby sighting without a shown status keeps what Player Search found.
        if (source != "Nearby" || ShownStatuses.Any(hasStatus))
        {
            var statusSheet = DataManager.GetExcelSheet<OnlineStatus>();
            p.Status = string.Join(", ", ShownStatuses.Where(hasStatus)
                .Select(id => statusSheet.GetRowOrDefault(id)?.Name.ExtractText() ?? "")
                .Where(s => s.Length > 0)
                .Distinct());
            p.IsNewOrReturning = NewOrReturningStatuses.Any(hasStatus);
        }

        p.Unavailable = hasStatus(InDutyStatus) ? "In duty" : hasStatus(BusyStatus) ? "Busy" : "";
        p.Source = source;
        p.LastSeen = DateTime.Now;
        return true;
    }

    /// <summary>Marks a messaged player as "Replied" when they send you a tell.</summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.TellIncoming)
            return;

        string? key = null;
        if (message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault() is { } player)
        {
            key = $"{player.PlayerName}@{player.World.ValueNullable?.Name.ExtractText()}";
        }
        else
        {
            // No player link in the sender: match by name alone if exactly one messaged player has it.
            var name = message.Sender.TextValue.Trim();
            var matches = Config.Messaged.Where(k => k.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count == 1)
                key = matches[0];
        }

        if (key == null || !Config.Messaged.Contains(key))
            return;

        var text = message.Message.TextValue.Trim();
        Config.Replied[key] = DateTime.Now;
        Config.ReplyText[key] = text;
        // A later clear "yes" or "no" updates the answer; an unclear message keeps the previous one.
        if (ReplyClassifier.Classify(text) is { } answer)
            Config.ReplyAnswer[key] = answer;
        SaveConfig();
        Log.Information($"{key} replied");

        if (Config.NotifyReplies)
            NotifyReply(key, Config.ReplyAnswer.GetValueOrDefault(key), text);
    }

    private static void NotifyReply(string key, string? answer, string text)
    {
        // UI colour rows: 45 green, 17 red, 25 yellow.
        var (label, color) = answer switch
        {
            ReplyClassifier.Yes => ("YES", (ushort)45),
            ReplyClassifier.No => ("no", (ushort)17),
            _ => ("replied", (ushort)25),
        };
        ChatGui.Print(new SeStringBuilder()
            .AddText("[FC-less Finder] ")
            .AddUiForeground($"{key} {label}", color)
            .AddText($": {text}")
            .Build());
        Notifications.AddNotification(new Notification
        {
            Title = $"{key} replied",
            Content = text,
            Type = answer == ReplyClassifier.Yes ? NotificationType.Success : NotificationType.Info,
        });
    }

    public const string Invited = "Invited";
    public const string Joined = "Joined";

    public void SetProgress(string key, string? progress)
    {
        if (progress == null)
            Config.Progress.Remove(key);
        else
            Config.Progress[key] = progress;
        SaveConfig();
    }

    /// <summary>Your own FC tag, without the « » around it; null when you're not in an FC or not logged in.</summary>
    private static string? MyFcTag => ObjectTable.LocalPlayer is { } me ? NormalizeTag(me.CompanyTag.TextValue) : null;

    private static string NormalizeTag(string tag) => tag.Trim().Trim('«', '»', '<', '>').Trim();

    /// <summary>Adds an "FC-less Finder" submenu when right-clicking a player (chat name, party list, friend list, character).</summary>
    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target)
            return;
        var name = target.TargetName;
        var worldId = (ushort)target.TargetHomeWorld.RowId;
        var world = target.TargetHomeWorld.ValueNullable?.Name.ExtractText() ?? "";
        if (string.IsNullOrWhiteSpace(name) || worldId == 0 || world.Length == 0)
            return;
        if (ObjectTable.LocalPlayer is { } me && me.Name.TextValue == name && me.HomeWorld.RowId == worldId)
            return;

        var key = $"{name}@{world}";
        var items = new List<IMenuItem>();
        if (Config.Ignored.Contains(key))
            items.Add(Item("Unignore", () => { Config.Ignored.Remove(key); SaveConfig(); }));
        else
            items.Add(Item("Ignore", () => { Config.Ignored.Add(key); SaveConfig(); ChatGui.Print($"[FC-less Finder] Ignoring {key}."); }));
        if (!Roster.ContainsKey(key))
            items.Add(Item("Add to list", () =>
            {
                Roster[key] = new PlayerEntry { Name = name, HomeWorldId = worldId, HomeWorld = world, Source = "Manual", LastSeen = DateTime.Now };
                ChatGui.Print($"[FC-less Finder] Added {key} to the list.");
            }));
        if (Config.Messaged.Contains(key) && Config.Progress.GetValueOrDefault(key) != Joined)
        {
            if (Config.Progress.GetValueOrDefault(key) != Invited)
                items.Add(Item("Mark invited", () => SetProgress(key, Invited)));
            items.Add(Item("Mark joined", () => SetProgress(key, Joined)));
        }

        args.AddMenuItem(new MenuItem
        {
            Name = "FC-less Finder",
            PrefixChar = 'F',
            IsSubmenu = true,
            OnClicked = clicked => clicked.OpenSubmenu(items),
        });
    }

    private static MenuItem Item(string name, System.Action onClick) => new() { Name = name, OnClicked = _ => onClick() };

    public void SetAnswer(string key, string? answer)
    {
        if (answer == null)
            Config.ReplyAnswer.Remove(key);
        else
            Config.ReplyAnswer[key] = answer;
        SaveConfig();
    }

    public TimeSpan CooldownRemaining
    {
        get
        {
            var left = LastTellSent + TimeSpan.FromSeconds(Math.Max(3, Config.CooldownSeconds)) - DateTime.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Non-empty templates, in order. Tells rotate through them.</summary>
    private List<string> UsableTemplates => Config.MessageTemplates.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

    /// <summary>Builds the tell the next click would send (uses the current template in the rotation).</summary>
    public string BuildMessage(PlayerEntry p)
    {
        var templates = UsableTemplates;
        if (templates.Count == 0)
            return "";
        var text = templates[Config.NextTemplate % templates.Count]
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

        if (Config.Messaged.Contains(p.Key) && !Config.CanRetell(p.Key))
        {
            error = $"{p.Name} was already messaged.";
            return false;
        }

        if (p.Unavailable.Length > 0)
        {
            error = $"{p.Name} was {p.Unavailable.ToLowerInvariant()} when last seen.";
            return false;
        }

        LastTellSent = DateTime.Now;
        Config.Messaged.Add(p.Key);
        Config.MessagedAt[p.Key] = DateTime.Now;
        Config.TellCount[p.Key] = Config.TellCount.GetValueOrDefault(p.Key) + 1;
        Config.NextTemplate = (Config.NextTemplate + 1) % UsableTemplates.Count;
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
