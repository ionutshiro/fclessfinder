using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FcLessFinder;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Tell text. {name} = full name, {first} = first name.</summary>
    public string MessageTemplate { get; set; } = "Hi {first}! We're recruiting for our Free Company, would you like to join?";

    /// <summary>Minimum seconds between two tells sent by the plugin.</summary>
    public int CooldownSeconds { get; set; } = 5;

    public bool OnlyMyHomeWorld { get; set; } = true;
    public bool HideMessaged { get; set; } = true;
    public bool CollectNearby { get; set; } = true;

    /// <summary>"Name@World" keys of players already sent a tell, so nobody gets messaged twice.</summary>
    public HashSet<string> Messaged { get; set; } = [];
}
