# Direct connect (Steam + GOG together)

Steam matchmaking only admits Steam accounts that own Cairn on Steam, so a GOG copy
can never enter a Steam lobby. Direct connect skips Steam entirely: the host opens a
UDP port and guests join with the host's address. A Steam copy and a GOG copy can
both host and both join.

Both players must run **the same CairnMP build** and **the same Cairn game version**.

## 1. Build the mod (once, on Windows)

Build on the Windows PC with the **Steam** copy. The build needs the game's Steamworks
reference, which a GOG copy may not include.

1. Install [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Install MelonLoader into Cairn (the
   [CairnMP MelonLoader build](https://github.com/yutho-o/MelonLoader/releases/tag/v0.7.2-cairnmp.1),
   `MelonLoader.x64.zip` extracted next to `Cairn.exe`), then start Cairn once and
   quit. That creates `MelonLoader\Il2CppAssemblies` and `MelonLoader\net6`.
3. Build, replacing the folder with your Cairn install:

   ```powershell
   $cairn = "C:\Program Files (x86)\Steam\steamapps\common\Cairn"
   $env:MELON_LOADER_NET6_DIR = "$cairn\MelonLoader\net6"
   dotnet build CairnMultiplayerMod\CairnMultiplayerMod.csproj -c Release `
     -p:Il2CppAssembliesDir="$cairn\MelonLoader\Il2CppAssemblies" -p:DeployMod=false
   ```

   To skip the variable and property on later builds, link `game-refs\` to the
   install once instead (see
   [Reference assemblies](../README.md#reference-assemblies)).

4. Collect these files from `CairnMultiplayerMod\bin\Release\net6.0\`:
   `CairnMultiplayerMod.dll`, `CairnMultiplayerShared.dll`, `LiteNetLib.dll`,
   `Concentus.dll`, `NAudio.Core.dll`, `NAudio.Wasapi.dll`, plus
   `THIRD-PARTY-NOTICES.txt` from the repository root.

## 2. Install on both PCs

On each PC (Steam and GOG):

1. Install the same MelonLoader build into the Cairn folder and run the game once.
2. Copy the files from step 1.4 into `<Cairn folder>\Mods\`.

## 3. Connect the two PCs

You need a route between the two machines. The easiest one is
[Tailscale](https://tailscale.com) (free): no router setup needed.

1. Both install Tailscale and sign in.
2. The host shares their machine with the guest (Tailscale admin console → the
   machine → **Share**), or both join the same tailnet.
3. When the host starts hosting, Windows asks whether Cairn may use the network.
   Allow it. If no prompt appears, allow UDP port 27420 for `Cairn.exe` in
   Windows Defender Firewall.

Without Tailscale, the host can forward **UDP 27420** on their router and give out
their public IP instead.

## 4. Play

- **Host:** Multiplayer → Host → set visibility to **Direct IP** → create. The
  lobby code shows the address to share, such as `100.101.102.103:27420`. Use the
  copy button and send it to your friend.
- **Guest:** Multiplayer → Join → paste the address into the code field → Join.
  Any code containing `.` or `:` is treated as an address.

On a GOG copy, Steam cannot start for Cairn, so hosting always uses Direct IP
automatically. The Steam lobby browser and room codes stay available to Steam
players for Steam-only lobbies.

## Dropped connections

If the connection dies **without a goodbye** (Wi-Fi drop, router restart, Tailscale
switching routes, a game crash), the session is not over yet:

- **Guest:** keeps redialling the host every few seconds for up to **5 minutes**.
  The status line and chat say *Connection to the host lost. Reconnecting…*
- **Host:** keeps the guest's seat and climber for 5 minutes. Their name shows
  *(reconnecting…)* for everyone, and nobody else can take the seat.
- **When the connection is back,** the guest returns to their own seat under the same
  player id. The host then resends what a late joiner gets: pitons, rope links and
  extension state. Positions, weather and time are streamed continuously anyway. Both
  sides see a *reconnected* message.
- **If the host's game crashed** and they host again at the same address within the
  5 minutes, the guest's redial joins the new lobby automatically.
- **After 5 minutes** the guest returns to the menu and the host frees the seat.

A drop takes about 15 seconds to detect. Leaving on purpose (Leave button, quitting
Cairn normally, the host closing the lobby) still ends the connection immediately.

## Settings

`UserData\MelonPreferences.cfg`:

```toml
[DirectConnect]
Port = 27420      # UDP port the host listens on
Password = ""     # if set, the guest must set the same value
ReconnectSeconds = 300  # how long a dropped connection is retried / its seat kept; 0 = off

[Player]
Name = "Player"   # your name in a direct lobby (defaults to your Windows user name)
```

## Troubleshooting

| Message | Meaning |
| --- | --- |
| *The host did not answer…* | Address wrong, host not hosting, or the port is blocked. Check that both PCs can ping each other's Tailscale IP. |
| *Mod version … is incompatible* | The two of you are not running the same build. |
| *Could not open UDP port 27420* | Something else uses the port. Change `Port` and give your friend the new address. |
| *Wrong lobby password.* | The `Password` values differ. |
