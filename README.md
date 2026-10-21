# FC-less Finder (Dalamud plugin)

Finds players without a Free Company tag on your world and lets you send each of them a templated `/tell`.

## How it works

- **Player Search results**: run searches in the game's own *Social > Player Search* window. Every result without an FC tag is added to the plugin's list. The game returns at most 200 results per search, so vary area, job, level range, and language to cover more of your world.
- **Nearby players**: players around you without an FC tag are added automatically (toggleable).
- **Tells**: each row has a **Tell** button that sends one `/tell Name@World <your message>`. `{name}` and `{first}` in the template are replaced. A cooldown (minimum 3 s) applies between tells, and messaged players are remembered so nobody is messaged twice.

There is deliberately no "message everyone" button: automated mass tells break Dalamud's plugin rules and can get an account flagged for spam.

Open the window with `/fcless`.

## Install

In game, open `/xlsettings` > *Experimental* > *Custom Plugin Repositories*, add this URL, tick it, and save:

```
https://raw.githubusercontent.com/ionutshiro/FcLessFinder/main/repo.json
```

Then install **FC-less Finder** from `/xlplugins`.

## Build

Requires the .NET 10 SDK and Dalamud dev assemblies (installed by XIVLauncher, or set `DALAMUD_HOME`).

```
dotnet build FcLessFinder/FcLessFinder.csproj -c Release
```

Then add `FcLessFinder/bin/Release/FcLessFinder.dll` under *Dalamud Settings > Experimental > Dev Plugin Locations* and enable it in the plugin installer's Dev Tools tab.
