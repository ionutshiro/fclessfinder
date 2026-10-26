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

    /// <summary>Keep players whose answer is No out of the Players list (they stay in the Replies tab).</summary>
    public bool HideRefused { get; set; } = true;

    /// <summary>Hide players not seen in a search or nearby within this many minutes; they may have logged off.</summary>
    public bool OnlyRecentlySeen { get; set; } = true;
    public int RecentMinutes { get; set; } = 20;

    /// <summary>"Name@World" keys of players already sent a tell, so nobody gets messaged twice.</summary>
    public HashSet<string> Messaged { get; set; } = [];

    /// <summary>"Name@World" keys of players you chose to ignore; they never show in the list.</summary>
    public HashSet<string> Ignored { get; set; } = [];

    /// <summary>"Name@World" keys of messaged players who sent a tell back, with when they replied.</summary>
    public Dictionary<string, DateTime> Replied { get; set; } = [];

    /// <summary>Last tell each messaged player sent back.</summary>
    public Dictionary<string, string> ReplyText { get; set; } = [];

    /// <summary>"Yes" or "No" per player; guessed from the reply, can be changed by hand. Missing = unclear.</summary>
    public Dictionary<string, string> ReplyAnswer { get; set; } = [];

    /// <summary>When each messaged player was last sent a tell.</summary>
    public Dictionary<string, DateTime> MessagedAt { get; set; } = [];

    /// <summary>How many tells each messaged player has been sent (at most <see cref="MaxTellsPerPlayer"/>).</summary>
    public Dictionary<string, int> TellCount { get; set; } = [];

    /// <summary>Players who never answered can get one more tell after this many days.</summary>
    public bool AllowRetell { get; set; } = true;
    public int RetellAfterDays { get; set; } = 7;
    public const int MaxTellsPerPlayer = 2;

    /// <summary>"Invited" or "Joined" per player, after they've been messaged.</summary>
    public Dictionary<string, string> Progress { get; set; } = [];

    /// <summary>Print a chat line and show a pop-up when a messaged player replies.</summary>
    public bool NotifyReplies { get; set; } = true;

    public void Migrate()
    {
        if (MessageTemplates.Count == 0)
            MessageTemplates.Add(string.IsNullOrWhiteSpace(MessageTemplate) ? DefaultMessage : MessageTemplate);
        MessageTemplate = null;

        // Players messaged before v0.6 have no send time; count from now so a re-tell waits the full delay.
        foreach (var key in Messaged)
        {
            MessagedAt.TryAdd(key, DateTime.Now);
            TellCount.TryAdd(key, 1);
        }
    }

    /// <summary>
    /// True when a messaged player never replied, isn't invited or joined, had only one tell,
    /// and the re-tell delay has passed.
    /// </summary>
    public bool CanRetell(string key) =>
        AllowRetell
        && Messaged.Contains(key)
        && !Replied.ContainsKey(key)
        && !Progress.ContainsKey(key)
        && TellCount.GetValueOrDefault(key, 1) < MaxTellsPerPlayer
        && DateTime.Now - MessagedAt.GetValueOrDefault(key, DateTime.Now) >= TimeSpan.FromDays(RetellAfterDays);
}
