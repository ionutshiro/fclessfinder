using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FcLessFinder;

public sealed class MainWindow : Window
{
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

        var template = config.MessageTemplate;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##template", ref template, 400))
        {
            config.MessageTemplate = template;
            plugin.SaveConfig();
        }
        ImGui.TextDisabled("Tell text. Placeholders: {name}, {first}");

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
            plugin.SaveConfig();
        }

        var homeWorld = plugin.LocalHomeWorldId;
        var rows = plugin.Roster.Values
            .Where(p => !config.OnlyMyHomeWorld || homeWorld == null || p.HomeWorldId == homeWorld)
            .Where(p => !config.HideMessaged || !config.Messaged.Contains(p.Key))
            .Where(p => filter.Length == 0
                        || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Job.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Status.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastSeen)
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
            if (config.Messaged.Contains(p.Key))
            {
                ImGui.TextDisabled("Sent");
            }
            else
            {
                if (cd > TimeSpan.Zero) ImGui.BeginDisabled();
                if (ImGui.SmallButton("Tell"))
                    status = plugin.SendTell(p, out var error) ? $"Sent tell to {p.Key}." : error;
                if (cd > TimeSpan.Zero) ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(plugin.BuildMessage(p));
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
    }
}
