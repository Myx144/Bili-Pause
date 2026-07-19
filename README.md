# BiliPause

(Written by Codex)

A Windows utility that lets you control a Bilibili video playing in the background while gaming — without ever leaving your game window.

Press **Mouse4** (side back button) to pause/play, **Mouse5** (side forward button) to skip back. Works while Genshin Impact (or any game window) is in the foreground.

## How It Works

1. A global low-level mouse hook (`WH_MOUSE_LL`) catches side-button presses anywhere, including fullscreen games
2. The hook checks if your game window is currently the foreground window
3. If yes, it uses `AttachThreadInput` + `SetFocus` to silently give keyboard focus to the Bilibili window's Chromium render widget — **without activating the window** (no flash, no focus steal)
4. `SendMessage(WM_KEYDOWN)` / `WM_KEYUP` sends the keystroke to Bilibili
5. The mouse event is consumed (`return 1` from the hook) so the game never sees it

## Features

- **Mouse4** → Space (pause / play)
- **Mouse5** → ← Left arrow (video back 10s)
- **Ctrl + Mouse4/5** → always sends the key regardless of foreground window (emergency pause)
- **Foreground check** — only triggers when your game window is active
- **Persistent config** — remembers selected windows by executable name, not title
- **Minimize to tray** — first time prompts whether to hide to system tray; remembers preference
- **Custom icon** — drop an `app.ico` next to the exe

## Screenshot

```
┌────────────────────────────────────────────┐
│  BiliPause                            ─  ✕ │
│                                            │
│  Mouse4 Pause/Play     Mouse5 Video back   │
│                                            │
│  ┌──────────────────────────────────────┐  │
│  │ Game  [ Yuanshen                ] [Select] │
│  │ Video [ bilibili - xxxxx        ] [Select] │
│  └──────────────────────────────────────┘  │
│                                            │
│  ● Ready                        [ Start ]   │
│                                            │
└────────────────────────────────────────────┘
```

## Usage

1. **Open Bilibili** in your browser or desktop app, start a walkthrough video
2. **Launch Genshin Impact** (or any game)
3. Right-click `start_bili_pause.bat` → **Run as Administrator** (required for the global mouse hook)
4. The app auto-detects both windows by title. If not, click **Select** next to each and click the target window
5. Press **Start**
6. Play your game. Mouse4 pauses the video, Mouse5 rewinds

### System Tray

Click **─** (minimize) — on first use, a dialog asks "Minimize to system tray?" Choose **Yes** to hide to tray (right-click tray icon → Show / Exit). The preference is remembered.

## Requirements

- Windows 10 (1803+) or Windows 11
- .NET 8 Runtime (included in the self-contained build)

## Build from Source

```bash
# Requires .NET 8 SDK
cd BiliPauseWpf
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The self-contained exe lands in `publish\BiliPauseWpf.exe`.

### Project Structure

```
BiliPauseWpf/
├── App.xaml / App.xaml.cs        # WPF application entry
├── MainWindow.xaml               # UI layout
├── MainWindow.xaml.cs            # Hook logic + P/Invoke
├── BiliPauseWpf.csproj           # .NET 8 WPF project
├── app.manifest                  # DPI awareness + Common Controls v6
└── publish/
    └── BiliPauseWpf.exe          # Self-contained single-file build
```

## Technical Details

The core challenge is sending keyboard input to a **background Chromium window**. `PostMessage(WM_KEYDOWN)` alone doesn't work — Chromium uses Raw Input and ignores synthesized window messages from external processes when the window isn't focused.

The solution:
1. `GetWindowThreadProcessId` for both our thread and the target
2. `AttachThreadInput(ourTid, targetTid, TRUE)` — merges input queues
3. `SetFocus(Chrome_RenderWidgetHostHWND)` — silently transfers keyboard focus (no foreground activation because threads are attached)
4. `SendMessage(WM_KEYDOWN)` / `WM_KEYUP` — Chromium now processes these because its render widget believes it has focus
5. Restore focus, detach threads

The target is the `Chrome_RenderWidgetHostHWND` child window inside the Bilibili window — this is the actual Chromium input surface.

Window persistence uses `GetWindowThreadProcessId` + `OpenProcess` + `QueryFullProcessImageNameW` to get the executable filename, stored in `bili_pause.ini`.

## License

MIT
