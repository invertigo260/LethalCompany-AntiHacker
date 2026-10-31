# AntiHacker v2.0.2

A comprehensive host-side security and Quality-of-Life mod for **Lethal Company**. AntiHacker protects your lobbies from malicious players, prevents ban evasion, blocks game-breaking server RPC exploits (including remote scrap deletion, item vacuuming, and ship tampering), provides instant ship recovery tools, and adds convenient ship management commands.

---

## 🛡️ Anti-Cheat & Lobby Security

- **Ban Evasion Prevention**: When you kick a player from your lobby, they are immediately added to the ban list and blocked from reconnecting.
- **Lobby-Verified Steam ID Check (Anti-Spoofing)**: Many cheat clients forge their Steam ID during the connection handshake. AntiHacker cross-references connection requests against actual Steam lobby events. Anyone attempting to join with a spoofed ID is rejected and autobanned.
- **Duplicate & Impersonation Protection**: Blocks clients attempting to connect using a Steam ID that is already active in the session.
- **Persistent Blocklist**: Kicked/banned Steam IDs are automatically saved to `AntiHacker_KickList.txt` so repeat offenders stay blocked across game restarts.
- **Built-in & Custom Blacklists**: Includes a built-in blacklist of known griefers, plus a configurable `ExtraBlacklist` setting to add your own comma-separated Steam IDs.
- **Dynamic Vanilla Lobby Detection**: If AntiHacker is the only mod installed, your lobby can appear in the vanilla server list. If other mods are present, it marks the lobby as modded (can also be forced to vanilla via `ForceVanillaLobby`).

---

## 🔒 Server RPC Guards (Host-Side Exploit Prevention)

Cheaters often send unauthorized Server RPCs to crash or ruin the lobby. AntiHacker inspects incoming network calls from remote clients:

- **Scrap Deletion & Unheld Throw Exploit Protection**:
  - Blocks remote clients from invoking `ThrowObjectServerRpc` or `PlaceObjectServerRpc` on items they are not currently holding on the server.
  - Prevents hackers from remotely stripping `isInShipRoom = false` on scrap before ship takeoff (which causes vanilla `RoundManager` to despawn all scrap upon orbit).
  - Enforces distance checks on item throws/drops (`<= 20m`) and blocks malicious re-parenting of scrap onto outdoor moon objects/nests.
  - Automatically enforces `isInShipRoom = true` and `isInElevator = true` if an item is dropped inside the ship bounds, preventing spoofed outside drops.
- **Ship Scrap Lock (`LockShipScrapUntilCompany`)**:
  - Remote clients cannot pick up items with scrap value (`scrapValue > 0`) once they are inside the ship cabin, **unless** the ship is at the Company Building (`71 Gordion`) to sell. Host can always move scrap at any time.
- **Ship-Item Grab Rate Limiter (`MaxShipGrabsPerSecond`)**:
  - Limits how fast remote clients can grab items marked inside the ship (default: 3 grabs/sec). Excessive spam (> 8/sec) results in an instant autoban and kick.
  - Rate limiting is automatically disabled while on the Company Building moon so your crew can quickly unload scrap at the counter.
  - Client grab animations are cleanly rejected without desyncing player hands.
- **Ship Furniture, Light Switch & Terminal Protection**:
  - **Light Switch Lockout**: Remote clients cannot move or store the ship Light Switch in build mode.
  - **Terminal Protection**: Prevents remote clients from moving or storing the ship Terminal.
  - **Protected Upgrades**: Prevents remote clients from storing essential ship equipment (`Teleporter`, `Inverse Teleporter`).
  - **Void Placement Blocking**: Blocks remote clients from placing objects below `y <= -100` (void/hiding exploits) or outside ship bounds.
- **ChangeLevel Exploit Protection**: Prevents malicious clients from sending invalid moon/level IDs (a common exploit used to freeze the ship in permanent hyperspace). Attackers are immediately autobanned and disconnected.
- **Credit & Economy Safeguards**: Validates all client-initiated credit changes (`SyncGroupCreditsServerRpc`, `BuyItemsServerRpc`, `BuyVehicleServerRpc`, `BuyShipUnlockableServerRpc`). Drops illegitimate balance increases and autobans negative or wildly manipulated credit amounts.
- **Unlockable & Storage Validation**: Prevents remote clients from requesting invalid unlockable IDs.
- **Ship Lever Protection (`AllowClientStartEnd`)**: Host-configurable option to prevent clients from pulling the start/landing lever or manually ejecting the crew. When disabled, client lever attempts are safely ignored.
- **Hidden Debug RPC Blocking**: Instantly blocks and autobans clients attempting to invoke internal debug RPCs (`Debug_EnableTestRoomServerRpc`, `Debug_ToggleAllowDeathServerRpc`, `Debug_ReviveAllPlayersServerRpc`).
- **Spam & Flooding Rate Limiting**: Limits object placement and storage RPCs to prevent lag machines and physics desyncs.

---

## 💻 Terminal Commands

Type these directly into the ship terminal:

| Command | Who | Description |
| :--- | :--- | :--- |
| `fixship` | Host only | Clears a stuck travel lock left behind by an exploited or interrupted `ChangeLevel`, re-enabling the start lever and terminal. |
| `money <amount>` | Host only | **Disabled by default; recovery tool only** (enable with `EnableMoneyCommand`). Intended solely for repairing credits a hacker has tampered with. Sets group credits (e.g. `money 500`). Can also adjust relatively (e.g. `money +250` or `money -100`). |
| `clean all` | Host only | Automatically sends all eligible ship furniture/decor to storage (skips items containing scrap, light switch, and the terminal). |
| `clean <name>` | Host only | Sends specific furniture matching the name to storage (e.g. `clean bunk`, `clean table`). |

---

## 🛠️ Quality of Life & Build Controls

- **Ship Recovery Hotkey (`F9`)**: Host can press **F9** at any time to clear a stuck ship travel lock (`travellingToNewLevel`), restore lever interaction, and reset terminal cooldowns.
- **Move / Store Terminal (`F8`)**: Allows the host to store the ship terminal away or un-store it! Pressing **F8** toggles the terminal into storage, or unstores it straight into placement mode with rotation restored.
- **Unstore Item Hotkey (`F6`)**: Grabs the first item from storage and immediately enters placement mode.
- **Ship Lights Hotkey (`F7`)**: Toggles the ship light switch from anywhere on the ship.
- **Advanced Furniture Rotation**:
  - While placing an object in build mode, use the **Arrow Keys** to rotate it in 90° increments along the X and Z axes (Up/Down for pitch, Left/Right for roll).
  - Press **End** to reset rotation back to standard default orientation (fixes upside-down items!).
- **Contextual Teleport (`F5`, Optional)**:
  - Disabled by default and intended only for getting out of trouble caused by hackers (e.g. being stranded or teleported away). When enabled via `EnableTeleport`, pressing **F5** inside the ship uses the inverse teleporter; pressing it outside beams you back to the ship.

---

## ⚙️ Configuration

Configuration is located at `BepInEx/config/com.invertigo.antihacker.cfg`:

### `[AntiHacker]`
- `LockShipScrapUntilCompany` (*default: `true`*): When enabled, remote clients cannot pick up items with scrap value inside the ship unless parked at the Company Building.
- `MaxShipGrabsPerSecond` (*default: `3`*): Max rate at which remote clients can grab ship items per second.
- `PersistKickList` (*default: `true`*): Whether kicked players remain blocked across game restarts.
- `ForceVanillaLobby` (*default: `false`*): Forces your server to appear in the vanilla server list regardless of other mods.
- `ExtraBlacklist` (*default: `""`*): Comma-separated list of Steam IDs to ban on startup.
- `AllowClientStartEnd` (*default: `true`*): If `false`, only the host can pull the start/stop lever. Remote client attempts are ignored.
- `EnableMoneyCommand` (*default: `false`*): Enables the host-only `money` terminal command. Off by default; only for fixing credits tampered with by hackers.
- `RecoveryKey` (*default: `F9`*): Key to un-freeze a stuck ship travel state.

### `[Hotkeys]`
- `MoveTerminalKey` (*default: `F8`*): Hotkey to store or unstore the terminal.
- `UnstoreKey` (*default: `F6`*): Hotkey to unstore the first item in storage and enter build mode.
- `LightSwitchKey` (*default: `F7`*): Hotkey to toggle ship lights.
- `RotationTipEveryTime` (*default: `false`*): If true, displays the rotation hint popup every time you place an object.
- `EnableTeleport` (*default: `false`*): Enable contextual F5 teleportation. Off by default; only for resolving issues caused by hackers.
- `TeleportKey` (*default: `F5`*): Hotkey for contextual teleportation.

---

## 📦 Installation

- **Mod Manager (Recommended)**: Install via **r2modman** or the **Thunderstore Mod Manager**.
- **Manual Installation**: Place `AntiHacker.dll` inside your `BepInEx/plugins/` directory.
