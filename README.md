# FallTracker (baby steps)

a melonloader mod for baby steps that counts the pain. every fall, how many metres it cost you, your biggest drop and how long you spent on the ground, for this session and for each save.

## what it does

- pause the game and the stats show on the left, under the title, in the menu's own style:
  - falls
  - metres lost (how much lower you got up than where you were standing)
  - last fall (its drop and time on the ground)
  - biggest drop
  - time on the ground
- two columns: this session, and this save (kept per save slot, works with more save slots too)
- teleports, loading a save and respawns don't count as drops
- when you quit, it writes a rage report to `UserData/FallTracker/reports/`
- a **FALL TRACKER** page in the pause menu (needs BabyStepsMenuLib, see install):
  - **reports**: read your rage reports one at a time and delete them (press delete twice)
  - **options**: show / hide the stats, delete all rage reports
- works with the gamepad on every row, even with other mods' pause menu pages installed (like the multiplayer mod)
- with my other mod rulebook also installed, both pages open from one **MODS** button, so the pause menu doesn't run off the screen

## install

1. install [melonloader](https://melonwiki.xyz/) 0.7.x for baby steps
2. drop `FallTracker.dll` in `Baby Steps/Mods/`
3. optional, for the FALL TRACKER page: [BabyStepsMenuLib](https://github.com/caleborchard/BabyStepsMenuLib) (also comes with the multiplayer mod). without it everything else still works

## files

everything is in `Baby Steps/UserData/FallTracker/`, the game's own files are never touched:

- `slot<N>.json` the totals for each save slot
- `slot<N>-falls.csv` where every fall started (time, position, drop, time down)
- `reports/` the rage reports
- `FallTracker.cfg` settings (show the stats or not)

## build

needs the .net 6 sdk and the game with melonloader installed (for the references).

```
dotnet build src/FallTracker.csproj -c Release -p:GameDir="C:\path\to\Baby Steps"
```

`BabyStepsMenuLib.dll` is expected in the game's `Mods` folder to build.

## credits

- baby steps by gabe cuzzillo, maxi boch and bennett foddy
- pause menu page made with [BabyStepsMenuLib](https://github.com/caleborchard/BabyStepsMenuLib) by caleb orchard (not included, used if installed)

tested on baby steps (steam) with melonloader 0.7.3, on steam deck.

not affiliated with or endorsed by gabe cuzzillo, maxi boch, bennett foddy or devolver digital.

## licence

cc0 1.0, do whatever you want with it. see `LICENSE`.
