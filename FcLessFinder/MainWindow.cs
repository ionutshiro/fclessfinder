using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FcLessFinder;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Red = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 Orange = new(1f, 0.7f, 0.3f, 1f);

    private readonly Plugin plugin;
    private string filter = "";
    private string status = "";

    public MainWindow(Plugin plugin) : base("FC-less Finder###FcLessFinderMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 360), MaximumSize = new Vector2(float.MaxValue) };
    }

    private Configuration Config => plugin.Config;

    public override void Draw()
    {
        // Shown above the tabs so tell results and the cooldown are visible from any tab.
        var cd = plugin.CooldownRemaining;
        if (cd > TimeSpan.Zero)
            ImGui.TextDisabled($"Next tell in {cd.TotalSeconds:0}s");
        if (status.Length > 0)
            ImGui.TextColored(Orange, status);

        if (!ImGui.BeginTabBar("##tabs"))
            return;

        if (ImGui.BeginTabItem("Players"))
        {
            DrawPlayersTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem($"Replies ({Config.Replied.Count})###repliesTab"))
        {
            DrawRepliesTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Messages"))
        {
            DrawMessagesTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawPlayersTab()
    {
        var config = Config;

        ImGui.TextWrapped("Open Social > Player Search and run searches (vary area, job, level, language to cover your world). "
                          + "Every result without an FC tag is added here. Players around you are added too.");

        if (ImGui.Button("Refresh"))
        {
            plugin.ScanNow();
            status = plugin.RefreshSearch() ?? "Search sent again; new results are added as they arrive.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Repeats your last Social > Player Search (same filters) and rescans nearby players.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##filter", "Filter: name, job or status", ref filter, 64);

        var onlyHome = config.OnlyMyHomeWorld;
        if (ImGui.Checkbox("Only my home world", ref onlyHome)) { config.OnlyMyHomeWorld = onlyHome; plugin.SaveConfig(); }
        ImGui.SameLine();
        var hideMessaged = config.HideMessaged;
        if (ImGui.Checkbox("Hide already messaged", ref hideMessaged)) { config.HideMessaged = hideMessaged; plugin.SaveConfig(); }
        ImGui.SameLine();
        var newFirst = config.NewAndReturningFirst;
        if (ImGui.Checkbox("New/returning first", ref newFirst)) { config.NewAndReturningFirst = newFirst; plugin.SaveConfig(); }
        ImGui.SameLine();
        var onlyNew = config.OnlyNewAndReturning;
        if (ImGui.Checkbox("Only new/returning", ref onlyNew)) { config.OnlyNewAndReturning = onlyNew; plugin.SaveConfig(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("New Adventurers and Returners.");
        ImGui.SameLine();
        var hideRefused = config.HideRefused;
        if (ImGui.Checkbox("Hide players who said no", ref hideRefused)) { config.HideRefused = hideRefused; plugin.SaveConfig(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Players whose answer is No. They're still listed in the Replies tab.");

        var homeWorld = plugin.LocalHomeWorldId;
        var rows = plugin.Roster.Values
            .Where(p => !config.Ignored.Contains(p.Key))
            .Where(p => !config.OnlyMyHomeWorld || homeWorld == null || p.HomeWorldId == homeWorld)
            // Players who replied, or who can be messaged again, stay visible even when messaged players are hidden.
            .Where(p => !config.HideMessaged || !config.Messaged.Contains(p.Key) || config.Replied.ContainsKey(p.Key) || config.CanRetell(p.Key))
            .Where(p => !config.OnlyNewAndReturning || p.IsNewOrReturning)
            .Where(p => !config.HideRefused || config.ReplyAnswer.GetValueOrDefault(p.Key) != ReplyClassifier.No)
            .Where(p => filter.Length == 0
                        || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Job.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Status.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => config.NewAndReturningFirst && p.IsNewOrReturning)
            .ThenByDescending(p => p.LastSeen)
            .ToList();

        ImGui.Text($"{rows.Count} players shown ({plugin.Roster.Count} collected).");
        ImGui.SameLine();
        ImGui.TextDisabled($"Current Player Search: {plugin.SearchResultCount} results, {plugin.SearchWithoutFcCount} without FC.");

        if (!ImGui.BeginTable("##roster", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableHeadersRow();

        var cd = plugin.CooldownRemaining;
        string? ignore = null;

        foreach (var p in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(p.Name);
            ImGui.TableNextColumn(); ImGui.Text(p.HomeWorld);
            ImGui.TableNextColumn(); ImGui.Text(p.Job);
            ImGui.TableNextColumn();
            if (p.Level > 0) ImGui.Text(p.Level.ToString());
            else ImGui.TextDisabled("?");
            if (p.Level == 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip("Player Search doesn't report level. It fills in once this player is near you.");
            ImGui.TableNextColumn(); ImGui.Text(p.Status);
            ImGui.TableNextColumn(); ImGui.Text(p.Source);
            ImGui.TableNextColumn(); ImGui.Text(p.LastSeen.ToString("HH:mm"));
            ImGui.TableNextColumn();

            ImGui.PushID(p.Key);
            if (config.Progress.TryGetValue(p.Key, out var progress))
            {
                ImGui.TextColored(Green, progress);
            }
            else if (config.Replied.ContainsKey(p.Key))
            {
                DrawAnswer(config.ReplyAnswer.GetValueOrDefault(p.Key));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(config.ReplyText.GetValueOrDefault(p.Key, ""));
            }
            else if (config.Messaged.Contains(p.Key) && !config.CanRetell(p.Key))
            {
                ImGui.TextDisabled("Sent");
                if (ImGui.IsItemHovered() && config.MessagedAt.TryGetValue(p.Key, out var sentAt))
                    ImGui.SetTooltip($"Sent {sentAt:dd MMM HH:mm}.");
            }
            else
            {
                var disabled = cd > TimeSpan.Zero || p.Unavailable.Length > 0;
                if (disabled) ImGui.BeginDisabled();
                var label = p.Unavailable.Length > 0 ? p.Unavailable : config.Messaged.Contains(p.Key) ? "Tell again" : "Tell";
                if (ImGui.SmallButton(label))
                    status = plugin.SendTell(p, out var error) ? $"Sent tell to {p.Key}." : error;
                if (disabled) ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(p.Unavailable.Length > 0
                        ? $"{p.Unavailable} when last seen. Press Refresh to check again."
                        : plugin.BuildMessage(p));
            }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Ignore"))
                ignore = p.Key;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hide this player from the list. Undo in Settings > Ignored players.");
            ImGui.PopID();
        }

        ImGui.EndTable();

        // Changed after the loop so the list isn't modified while it's being drawn.
        if (ignore != null)
        {
            config.Ignored.Add(ignore);
            plugin.SaveConfig();
            status = $"Ignoring {ignore}.";
        }
    }

    private void DrawRepliesTab()
    {
        var config = Config;
        if (config.Replied.Count == 0)
        {
            ImGui.TextDisabled("No replies yet. When someone you messaged sends you a tell, it shows up here.");
            return;
        }

        var yesCount = config.ReplyAnswer.Values.Count(a => a == ReplyClassifier.Yes);
        var noCount = config.ReplyAnswer.Values.Count(a => a == ReplyClassifier.No);
        var joined = config.Progress.Values.Count(v => v == Plugin.Joined);
        ImGui.Text($"{config.Replied.Count} replies: {yesCount} yes, {noCount} no, {joined} joined.");

        if (!ImGui.BeginTable("##replies", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Answer", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Their reply", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Invite", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableHeadersRow();

        foreach (var (key, when) in config.Replied.OrderByDescending(r => r.Value).ToList())
        {
            ImGui.PushID(key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(key);
            ImGui.TableNextColumn(); DrawAnswer(config.ReplyAnswer.GetValueOrDefault(key));
            ImGui.TableNextColumn(); ImGui.TextWrapped(config.ReplyText.GetValueOrDefault(key, ""));
            ImGui.TableNextColumn(); ImGui.Text(when.ToString("dd MMM HH:mm"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Y")) plugin.SetAnswer(key, ReplyClassifier.Yes);
            ImGui.SameLine();
            if (ImGui.SmallButton("N")) plugin.SetAnswer(key, ReplyClassifier.No);
            ImGui.SameLine();
            if (ImGui.SmallButton("?")) plugin.SetAnswer(key, null);
            ImGui.TableNextColumn(); DrawProgress(key);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawMessagesTab()
    {
        var config = Config;

        ImGui.TextWrapped("Each tell uses the next message in turn. Placeholders: {name} = full name, {first} = first name.");
        var removeAt = -1;
        for (var i = 0; i < config.MessageTemplates.Count; i++)
        {
            ImGui.PushID(i);
            var template = config.MessageTemplates[i];
            ImGui.SetNextItemWidth(-30);
            if (ImGui.InputText("##template", ref template, 400))
            {
                config.MessageTemplates[i] = template;
                plugin.SaveConfig();
            }
            ImGui.SameLine();
            if (config.MessageTemplates.Count == 1) ImGui.BeginDisabled();
            if (ImGui.Button("X"))
                removeAt = i;
            if (config.MessageTemplates.Count == 1) ImGui.EndDisabled();
            ImGui.PopID();
        }
        if (removeAt >= 0)
        {
            config.MessageTemplates.RemoveAt(removeAt);
            plugin.SaveConfig();
        }
        if (config.MessageTemplates.Count < 5 && ImGui.Button("Add message"))
        {
            config.MessageTemplates.Add("");
            plugin.SaveConfig();
        }

        ImGui.Spacing();
        var cooldown = config.CooldownSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Seconds between tells", ref cooldown, 3, 60))
        {
            config.CooldownSeconds = cooldown;
            plugin.SaveConfig();
        }
    }

    private void DrawSettingsTab()
    {
        var config = Config;

        var nearby = config.CollectNearby;
        if (ImGui.Checkbox("Collect nearby players", ref nearby)) { config.CollectNearby = nearby; plugin.SaveConfig(); }

        var notify = config.NotifyReplies;
        if (ImGui.Checkbox("Alert me when someone replies", ref notify)) { config.NotifyReplies = notify; plugin.SaveConfig(); }

        var retell = config.AllowRetell;
        if (ImGui.Checkbox("Allow one more tell to players who didn't answer, after", ref retell)) { config.AllowRetell = retell; plugin.SaveConfig(); }
        ImGui.SameLine();
        var days = config.RetellAfterDays;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("days", ref days, 1, 30)) { config.RetellAfterDays = days; plugin.SaveConfig(); }

        ImGui.Separator();
        ImGui.Text($"Ignored players ({config.Ignored.Count})");
        if (config.Ignored.Count == 0)
        {
            ImGui.TextDisabled("Nobody ignored. Use the Ignore button in the Players tab or the right-click menu in game.");
        }
        else
        {
            string? unignore = null;
            foreach (var key in config.Ignored.Order())
            {
                ImGui.PushID(key);
                if (ImGui.SmallButton("Unignore"))
                    unignore = key;
                ImGui.SameLine();
                ImGui.Text(key);
                ImGui.PopID();
            }
            if (unignore != null)
            {
                config.Ignored.Remove(unignore);
                plugin.SaveConfig();
            }
        }

        ImGui.Separator();
        ImGui.Text("Reset");
        if (ImGui.Button("Clear player list"))
            plugin.ClearRoster();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Empties the collected players. Messaged history, replies and ignored players are kept.");
        ImGui.SameLine();
        if (ImGui.Button("Forget messaged history"))
        {
            config.Messaged.Clear();
            config.Replied.Clear();
            config.ReplyText.Clear();
            config.ReplyAnswer.Clear();
            config.MessagedAt.Clear();
            config.TellCount.Clear();
            config.Progress.Clear();
            plugin.SaveConfig();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Forgets who you messaged, their replies and Invited/Joined. Ignored players are kept.");
    }

    /// <summary>Yes in green, No in red, unclear as a yellow "Replied".</summary>
    private static void DrawAnswer(string? answer)
    {
        if (answer == ReplyClassifier.Yes) ImGui.TextColored(Green, "Yes");
        else if (answer == ReplyClassifier.No) ImGui.TextColored(Red, "No");
        else ImGui.TextColored(Yellow, "Replied");
    }

    /// <summary>Invited / Joined buttons for a player who has been messaged.</summary>
    private void DrawProgress(string key)
    {
        var progress = Config.Progress.GetValueOrDefault(key);
        if (progress == Plugin.Joined)
        {
            ImGui.TextColored(Green, "Joined");
            ImGui.SameLine();
            if (ImGui.SmallButton("Undo")) plugin.SetProgress(key, null);
            return;
        }
        if (progress == Plugin.Invited)
            ImGui.TextColored(Yellow, "Invited");
        else if (ImGui.SmallButton("Invited"))
            plugin.SetProgress(key, Plugin.Invited);
        ImGui.SameLine();
        if (ImGui.SmallButton("Joined"))
            plugin.SetProgress(key, Plugin.Joined);
    }
}
