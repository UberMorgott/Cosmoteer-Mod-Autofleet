# Autofleet

Cosmoteer 0.30.5 C# mod that automates your own ships. You compose each ship's behaviour from modules
(Mine, Loot, Offload, Anchor-link, Avoidance, Retreat, Guard), save the composition as a named profile
("Miner", "Mothership", anything you like) and assign it to ships. Miners mine asteroids, fly to their
offload ship when the hold is full, unload and go back to work, while avoiding enemies.

## Requirements

- Yet Another Mod Loader (workshop 3577650065), installed and enabled. It also provides the only
  `0Harmony.dll` the loader accepts. This mod does not use Harmony and does not ship it.
- Do not run another mining/automation mod on the same ships: both would issue commands to them.

## Install / build

```powershell
dotnet build -c Release          # needs global tool: dotnet tool install -g BepInEx.AssemblyPublicizer.Cli
```

- The build publicizes `Cosmoteer.dll` / `HalflingCore.dll` from the game folder into `lib\` (gitignored).
  It compiles against the net10.0 reference pack (NuGet `Microsoft.NETCore.App.Ref` 10.0.0), so the
  .NET 8 SDK is enough.
- Deploy **only via `tools\deploy.ps1`** (pwsh 7; `-ModDir`, `-GameBin` override the defaults). It aborts if
  the game is running, then runs `qgate -All -Full`, `dotnet build -c Release`, the offline load check
  `tools\jitcheck` (JIT-compiles every method against the real game assemblies and calls
  `GameLoadInitializer()` on a temp copy of the deployed `*.json` user data), copies `Autofleet.dll`,
  `mod.rules`, `strings\*.rules` into `%USERPROFILE%\Saved Games\Cosmoteer\<steamid>\Mods\Autofleet` and
  verifies SHA256. It never touches `settings.rules`: add `Mods/Autofleet` to `EnabledMods` once by hand.
- **First launch after every rebuild:** open the in-game Mods list and **trust the libraries** of the
  mod. The loader pins each DLL by its MVID, and every build changes that ID.

## Usage

- Open the window with the menu-bar button (formation icon, top left) or **Ctrl+Alt+A**.
- Left: your fleet as a tree. Right: six tabs. The header always shows the selected ships, their state and
  **Pause / Resume**, **Unload now** and **Return home**.
- **Assignments**: follows the live game selection. Pick a profile, an anchor ship (any of your ships, or
  none), the offload target and the work zone (circle on the map or nearest). No logic settings here: a
  ship runs exactly its profile (no per-ship overrides). With several ships selected, differing values show
  as mixed and only the fields you touch change.
- **Profiles**: create, duplicate, rename and delete profiles. Add/remove/enable modules, set their
  priority, parameters and activation (always / only anchored / only unanchored). The cargo policy (what to
  collect, what to keep aboard: all or a minimum) belongs to the profile only. Profiles are
  live: any edit (rename included) applies to every ship using the profile at once, no apply button.
- Six starter profiles are included (Miner, Fighter, Cow, Builder, Trader, Escort guard). Modules that are
  not implemented yet (Hunt, Support, Build, Trade, Crew, Production) can be stored but never activate.
- **Construction / Logistics / Economy**: placeholders for later versions. **Settings**: local display
  preferences (circles, state icons, cargo % label).
- State icons above each automated ship use the game's own sprites and stay screen-aligned; the cargo %
  shows next to the icon while the ship travels, mines or flies to offload. Hover an icon for the status.
- A manual order pauses that ship's automation; it resumes 5 s after your orders finish (or waits for
  **Resume**, per profile). **Pause** is an explicit stop that survives save/load.
- The UI follows the game language (English, German, Spanish, French, Japanese, Portuguese (Brazil), Russian, Chinese (Simplified)).

## How it works

- One agent per automated ship. Modules propose work; an arbiter picks exactly one movement owner:
  manual order / pause > Avoidance escape > Retreat > temporary intents (unload now, return home) > task
  modules by priority (ties: nearer target, then profile order) > idle (follow the anchor / park at the
  offload target). A higher priority preempts only after a minimum commitment time.
- Every action is a vanilla command or transfer sent through `NetManager.EnqueueInput`, so multiplayer stays
  in lockstep. Only the owning client plans its ships: the real owner (`Ship.Metadata.PlayerIndex`), even when
  SharedShip lets teammates command a ship. Teammates see such a ship read-only ("Driven by <owner>"). A ship given
  to another player drops the old owner's assignment quietly; the new owner may assign it. While the owner is
  offline nobody drives the ship.
- Mining uses `SalvageCommand`; offloading uses the same sources/sinks search as the vanilla transfer window
  and `MPMoveResourcesInput`. Asteroids in stasis are found through their stasis records; their remaining
  yield is carried over when a mined rock re-enters stasis, so empty rocks are not revisited.
- The hold fill used for the return threshold is the cargo % you see. If the ship cannot take any more of
  the resource it is mining, it returns early and says so ("Hold full: no room for X").
- Stuck detection on every travel leg (less than 50 units in 8 s): reverse, sidestep, move off the obstacle,
  strip/shoot an asteroid it is wedged in, re-plan around it, give the target up; if still wedged the
  status says it needs help.

## Persistence

- `Autofleet.profiles.json`: your profile library. `Autofleet.prefs.json`: display preferences.
- `Autofleet.state.json`: working copy of the assignments. When the game saves, a copy bound to that save
  file is written to `Autofleet.saves\`; loading that exact save restores it, including which commands
  the mod had issued, so nothing is issued twice after a load.
- Older `MiningDrone.settings.json` files are migrated once into Miner assignments and left in place.
- Unknown fields written by a newer version are preserved; files with a newer schema are not overwritten.
- Log: `Autofleet.log` in the mod folder.

## Known limits

- Not verified in multiplayer.
- Offload needs a crew path through airlocks, as in vanilla.
- Retreat does not use FTL; the damage trigger for Retreat arrives with a later version.
- Another mod that issues commands to the same ships is treated as a manual order.
