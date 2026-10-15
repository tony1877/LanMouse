# LAN Mouse

Share one mouse, keyboard and clipboard between two or more Windows PCs (up to 9) on the same network.

<p align="center"><img src="docs/screenshot.png" alt="LAN Mouse window showing the screen arrangement map" width="440"></p>

## Features

- **Screen arrangement:** drag every PC's screens to where they really sit, free-hand, with full multi-monitor support. The cursor crosses wherever screens touch, including straight from one remote PC to another. Arrange on any PC and the rest follow.
- **Hotkeys:** Ctrl+Alt+1 to 9 jump straight to a PC (numbered left to right in your arrangement). Scroll Lock brings the cursor home.
- **Smooth handoff:** the pointer is hidden on the PC you're not using, no movement is lost when crossing, and speed is matched between screens with different display scaling.
- **Clipboard on paste:** text, pictures, files and folders. Copying only sends a description; the data moves when you actually paste, over its own connection so it never slows down your mouse and keyboard.
- **Automatic pairing:** type the same pairing key on every PC and they find each other.
- **Encrypted:** AES-GCM with a key derived from the pairing key.
- **Sign-in screen control (optional):** install a small privileged helper so you can type your PIN/password on the other PC's Windows sign-in screen. Off by default; enable it in Settings. See the security note there.

- **Win+L works remotely:** Windows ignores a synthesized Win+L, so the controlled PC locks itself when it sees it.

## Download

Grab `LanMouse.exe` from the [Releases](../../releases) page and run it on both PCs. It is a single self-contained file, no install or .NET runtime needed.

## Setup

1. Run `LanMouse.exe` on both PCs.
2. Open Settings and enter the same pairing key on both (or generate one and type it on the other PC).
3. Press **Connect** on both.
4. Drag the other PC's screens into place on the map. Set it on either PC and the other follows.
5. If they don't find each other, click **Allow through firewall** on both PCs.

Network ports: UDP 47824 (discovery), TCP 47823 (input), TCP 47825 (clipboard transfers).

## Build from source

Requires the .NET 8 SDK on Windows.

```
dotnet publish -c Release -o release
```

The single-file app ends up in `release\LanMouse.exe`.

## License

MIT. See [LICENSE](LICENSE).
