using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FcLessFinder;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const string DefaultMessage = "Hi {first}! We're recruiting for our Free Company, would you like to join?";

    public int Version { get; set; } = 0;

    /// <summary>Single tell text from v0.2 and earlier; moved into <see cref="MessageTemplates"/> on load.</summary>
    public string? MessageTemplate { get; set; }

    /// <summary>
    /// Tell texts, used in rotation. {name} = full name, {first} = first name.
    /// Starts empty on purpose: the JSON loader appends to an existing list, so defaults are added in <see cref="Migrate"/>.
    /// </summary>
    public List<string> MessageTemplates { get; set; } = [];

    /// <summary>Index of the template the next tell uses.</summary>
    public int NextTemplate { get; set; }

    /// <summary>Minimum seconds between two tells sent by the plugin.</summary>
    public int CooldownSeconds { get; set; } = 5;

    public bool OnlyMyHomeWorld { get; set; } = true;
    public bool HideMessaged { get; set; } = true;
    public bool CollectNearby { get; set; } = true;
    public bool NewAndReturningFirst { get; set; } = true;
    public bool OnlyNewAndReturning { get; set; }

    /// <summary>"Name@World" keys of players already sent a tell, so nobody gets messaged twice.</summary>
    public HashSet<string> Messaged { get; set; } = [];

    /// <summary>"Name@World" keys of messaged players who sent a tell back, with when they replied.</summary>
    public Dictionary<string, DateTime> Replied { get; set; } = [];

    public void Migrate()
    {
        if (MessageTemplates.Count == 0)
            MessageTemplates.Add(string.IsNullOrWhiteSpace(MessageTemplate) ? DefaultMessage : MessageTemplate);
        MessageTemplate = null;
    }
}
