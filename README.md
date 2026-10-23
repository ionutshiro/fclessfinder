# FC-less Finder (Dalamud plugin)

Finds players without a Free Company tag on your world and lets you send each of them a templated `/tell`.

## How it works

- **Player Search results**: run searches in the game's own *Social > Player Search* window. Every result without an FC tag is added to the plugin's list. The game returns at most 200 results per search, so vary area, job, level range, and language to cover more of your world.
- **Nearby players**: players around you without an FC tag are added automatically (toggleable).
- **Refresh**: repeats your last Player Search with the same filters (at most once every 10 s) and rescans nearby players.
- **Level and status**: the list shows New Adventurer, Returner, and Mentor status. Player Search doesn't report level, so level is shown only for players who have been near you.
- Search results the game marks as from another server are skipped, because the game doesn't send their FC tag.
- **New/returning first**: New Adventurers and Returners are sorted to the top, and can be shown on their own.
- **Free trials**: players with the Trial Adventurer status are left out, because free trial accounts can't join a Free Company.
- **Tells**: each row has a **Tell** button that sends one `/tell Name@World <your message>`. `{name}` and `{first}` in the message are replaced. You can write up to 5 messages; each tell uses the next one in turn. A cooldown (minimum 3 s) applies between tells, and messaged players are remembered so nobody is messaged twice.
- **Ignore**: each row has an **Ignore** button that hides that player for good. Ignored players are listed under *Ignored players*, where you can unignore them.
- **Saved list**: collected players are saved to `roster.json` in the plugin's config folder, so the list survives restarts.
- **Reply alerts**: when someone you messaged replies, a coloured line is printed in chat and a pop-up appears (toggleable).
- **Invited / Joined**: mark players as Invited or Joined from the Replies table or the right-click menu. Someone you messaged who later shows up wearing your FC tag is marked Joined automatically.
- **Tell again**: players who never answered can get one more tell after a set number of days (default 7). Nobody gets more than two tells.
- **Right-click menu**: right-click a player in game (chat name, character, party or friend list) for an *FC-less Finder* submenu: Ignore / Unignore, Add to list, Mark invited, Mark joined.
- **Busy / in duty**: the Tell button is greyed out for players who were Busy or in a duty when last seen. Refresh to check again.
- **Replies**: when someone you messaged sends you a tell, their reply is saved and listed under *Replies*. The plugin guesses **Yes** ("sure", "why not", "ok") or **No** ("no thanks", "not interested", "already have an FC") from common words; unclear replies show as **Replied**. Use the Y / N / ? buttons to correct a guess.

There is deliberately no "message everyone" button: automated mass tells break Dalamud's plugin rules and can get an account flagged for spam.

Open the window with `/fcless`.

## Install

In game, open `/xlsettings` > *Experimental* > *Custom Plugin Repositories*, add this URL, tick it, and save:

```
https://raw.githubusercontent.com/ionutshiro/fclessfinder/main/repo.json
```

Then install **FC-less Finder** from `/xlplugins`.

## Build

Requires the .NET 10 SDK and Dalamud dev assemblies (installed by XIVLauncher, or set `DALAMUD_HOME`).

```
dotnet build FcLessFinder/FcLessFinder.csproj -c Release
```

Then add `FcLessFinder/bin/Release/FcLessFinder.dll` under *Dalamud Settings > Experimental > Dev Plugin Locations* and enable it in the plugin installer's Dev Tools tab.
