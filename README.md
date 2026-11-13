# Obenseuer Co-op

Play Obenseuer with your friends. One player hosts their save, and up to three friends join their world through a Steam lobby.

The host's world is the real one. Guests play inside a copy of it, and everything they do goes through the host. A guest's own saves are never touched. The host keeps each guest's inventory and money, so a guest who rejoins later picks up where they left off.

## What is shared

- Lobby and joining through Steam invites, with friends-only lobbies of up to 4 players
- The host's clock and weather, plus a sleep vote: time only skips once everyone is in bed
- Other players, shown as characters with their name, look and pose, including sitting on seats
- Building: placing, moving and removing furniture
- Floor items, coins and containers, with a lock so only one player uses a container at a time
- Doors, locks, switches and other interactables
- Crafting stations, liquids and station power
- Shops, vending machines, dialogue and quests (only the host can start a quest)
- Free roam: players can split up and travel to different areas
- Elevators, trains, the sauna, slot machines and timed processes
- Death and respawn, plus a separate prison for each player
- Text chat, players on the map, and a players panel

## Requirements

- Obenseuer on Steam
- BepInEx 6 (Unity.Mono, x64) or BepInEx 5.4.23 or newer (Mono, x64)
- [Obenseuer Modding Framework](https://github.com/NomadsReach/Obenseuer-Modding-Framework) 0.9.0 or newer, including its WebUi module. Without WebUi you get a simpler lobby panel.

Everyone in a lobby needs the same version of this mod. A lobby refuses a player on a different version. Mixing BepInEx 5 and BepInEx 6 players is fine.

## Install

1. Install BepInEx and the Obenseuer Modding Framework.
2. Make a folder `BepInEx\plugins\ObenseuerCoop\` in the game folder.
3. Copy the DLL for your loader into it, along with the `web` folder:
   - BepInEx 6: `ObenseuerCoop.dll`
   - BepInEx 5: `ObenseuerCoop.BepInEx5.dll`

## Playing

1. The host opens Multiplayer in the main menu, presses Host lobby and then Invite friends.
2. Friends accept the Steam invite.
3. The host presses Load world and picks a save.
4. Once the host is in the world, friends press Join world. If several friends join at once, the host sends the world to them one at a time.

When the host leaves or closes the game, everyone else goes back to the main menu.

### Keys

| Key | What it does |
|---|---|
| F8 | Host a lobby |
| F7 | Leave the lobby |
| T | Open chat (`/help` lists the commands) |
| / | Open chat with a command started |
| F9 | Players panel (while paused) |
| F6 | Host only: ask everyone in other areas to come to you |
| F7 / F8 | When the host travels somewhere: go with them / stay |
| F7 / F8 | After dying: respawn outside your apartment door / load a save (host) or rejoin the host (guest) |

F8 and F7 can be rebound in the Mods menu under Mod Keybinds. The chat keys are in the config file.

## Settings

These are in `BepInEx\config\obenseuer.coop.cfg`.

| Setting | Default | What it does |
|---|---|---|
| `[Co-op] TimerSpeed` | 1 | Speeds up timed processes: upgrades, station crafting, furniture blueprints and plant growth. The host's value applies to everyone. 1 is normal speed. |
| `[Coop] SendLook` | true | Sends your character's look to the other players. |
| `[Map] ShowOtherPlayers` | true | Shows the other players on the map. The host is shown in gold. |
| `[Chat] OpenKey` | T | Opens chat. |
| `[Chat] OpenKeyAlt` | / | Opens chat with a slash already typed. |
| `[General] ClockSnapMinutes` | 5 | Guests only: if the clock drifts from the host's by more than this many game minutes, it jumps straight to the host's time. |
| `[General] ClockSmoothSeconds` | 2 | Guests only: how quickly a smaller clock drift is smoothed out. |
| `[Debug] Enabled` | true | Writes co-op details to the log. F10 turns it on and off in game. |

The log is in `BepInEx\logs\obenseuer.coop.log`. Please attach it if you report a problem.

## Known limits

- Thunder is rolled separately on each machine, so it can differ between players.
- A guest who is exhausted does not pass out.
- When you leave a session, your clock goes back to where it was when the session started.

## Building from source

You need the .NET SDK and the game with BepInEx and the Obenseuer Modding Framework installed.

    dotnet build -c Release

That builds the BepInEx 6 version to `bin\`. For BepInEx 5:

    dotnet build -c Release -p:Loader=BepInEx5

That one goes to `bin\be5\`.

The build looks for the game in the default Steam folder. Set it with `-p:GameDir=...`, the `OBENSEUER_DIR` environment variable, or a `GameDir.user.props` file in the repo root.

## License

MIT, see [LICENSE](LICENSE).
