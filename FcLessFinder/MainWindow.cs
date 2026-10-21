using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FcLessFinder;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private readonly Plugin plugin;
    private string filter = "";
    private string status = "";

    public MainWindow(Plugin plugin) : base("FC-less Finder###FcLessFinderMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 360), MaximumSize = new Vector2(float.MaxValue) };
    }

    public override void Draw()
    {
        var config = plugin.Config;

        ImGui.TextWrapped("Open Social > Player Search and run searches (vary area, job, level, language to cover your world). "
                          + "Every result without an FC tag is added here. Players around you are added too.");
        ImGui.Separator();

        ImGui.TextDisabled("Messages: each tell uses the next one in turn. Placeholders: {name}, {first}");
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

        var cooldown = config.CooldownSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Seconds between tells", ref cooldown, 3, 60))
        {
            config.CooldownSeconds = cooldown;
            plugin.SaveConfig();
        }

        var onlyHome = config.OnlyMyHomeWorld;
        if (ImGui.Checkbox("Only my home world", ref onlyHome)) { config.OnlyMyHomeWorld = onlyHome; plugin.SaveConfig(); }
        ImGui.SameLine();
        var hideMessaged = config.HideMessaged;
        if (ImGui.Checkbox("Hide already messaged", ref hideMessaged)) { config.HideMessaged = hideMessaged; plugin.SaveConfig(); }
        ImGui.SameLine();
        var nearby = config.CollectNearby;
        if (ImGui.Checkbox("Collect nearby players", ref nearby)) { config.CollectNearby = nearby; plugin.SaveConfig(); }

        var newFirst = config.NewAndReturningFirst;
        if (ImGui.Checkbox("New/returning players first", ref newFirst)) { config.NewAndReturningFirst = newFirst; plugin.SaveConfig(); }
        ImGui.SameLine();
        var onlyNew = config.OnlyNewAndReturning;
        if (ImGui.Checkbox("Only new/returning", ref onlyNew)) { config.OnlyNewAndReturning = onlyNew; plugin.SaveConfig(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("New Adventurers, Trial Adventurers and Returners.");

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
        ImGui.SameLine();
        if (ImGui.Button("Clear list"))
            plugin.Roster.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Forget messaged history"))
        {
            config.Messaged.Clear();
            config.Replied.Clear();
            plugin.SaveConfig();
        }

        var homeWorld = plugin.LocalHomeWorldId;
        var rows = plugin.Roster.Values
            .Where(p => !config.OnlyMyHomeWorld || homeWorld == null || p.HomeWorldId == homeWorld)
            // Players who replied stay visible even when messaged players are hidden.
            .Where(p => !config.HideMessaged || !config.Messaged.Contains(p.Key) || config.Replied.ContainsKey(p.Key))
            .Where(p => !config.OnlyNewAndReturning || p.IsNewOrReturning)
            .Where(p => filter.Length == 0
                        || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Job.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Status.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => config.NewAndReturningFirst && p.IsNewOrReturning)
            .ThenByDescending(p => p.LastSeen)
            .ToList();

        var cd = plugin.CooldownRemaining;
        ImGui.Text($"{rows.Count} players shown ({plugin.Roster.Count} collected).");
        ImGui.SameLine();
        ImGui.TextDisabled($"Current Player Search: {plugin.SearchResultCount} results, {plugin.SearchWithoutFcCount} without FC.");
        if (cd > TimeSpan.Zero)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(next tell in {cd.TotalSeconds:0}s)");
        }
        if (status.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), status);

        if (config.Replied.Count > 0 && ImGui.CollapsingHeader($"Replies ({config.Replied.Count})###replies"))
        {
            foreach (var (key, when) in config.Replied.OrderByDescending(r => r.Value))
                ImGui.TextColored(Green, $"{key}  {when:dd MMM HH:mm}");
        }

        if (!ImGui.BeginTable("##roster", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

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
            if (config.Replied.ContainsKey(p.Key))
            {
                ImGui.TextColored(Green, "Replied");
            }
            else if (config.Messaged.Contains(p.Key))
            {
                ImGui.TextDisabled("Sent");
            }
            else
            {
                var disabled = cd > TimeSpan.Zero || p.Unavailable.Length > 0;
                if (disabled) ImGui.BeginDisabled();
                if (ImGui.SmallButton(p.Unavailable.Length > 0 ? p.Unavailable : "Tell"))
                    status = plugin.SendTell(p, out var error) ? $"Sent tell to {p.Key}." : error;
                if (disabled) ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(p.Unavailable.Length > 0
                        ? $"{p.Unavailable} when last seen. Press Refresh to check again."
                        : plugin.BuildMessage(p));
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
    }
}
