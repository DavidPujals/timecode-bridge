# Timecode Bridge — SMPTE LTC → Art-Net

Reads SMPTE linear timecode (LTC) from any Windows audio input — **ASIO**, **WASAPI**,
or **WDM/WaveIn** — and broadcasts it as **Art-Net ArtTimeCode** (UDP 6454) with
sub-frame latency. Built for show-critical use.

**Ready-to-run build:** `dist\TimecodeBridge.exe` (self-contained — no .NET install
required on the target machine).

## Quick start

1. Run `dist\TimecodeBridge.exe` — the bridge is **always on**: it starts listening
   immediately and translates the moment timecode appears. There is no start button.
2. **Audio input** — pick the driver (ASIO for lowest latency), the device, and the
   channel carrying LTC.
3. **Art-Net output** — pick the network interface facing your lighting network, and
   the target IP: `255.255.255.255` broadcasts to everything; use the console's IP
   for unicast (recommended on busy networks).

Any setting change is applied automatically about half a second after you make it.
Settings persist in `config.json` next to the exe.

**Runs in the background:** closing the window minimises to the system tray — the
tray dot shows live status (grey no signal · green locked · amber freewheel) and its
tooltip shows the current timecode. Double-click the tray icon (or launch the exe
again) to reopen the window; **right-click → Quit** to actually exit. Drop a shortcut
in `shell:startup` for unattended show machines.

## Behaviour details

- **Frame rate** is auto-detected (24 / 25 / 29.97 drop-frame / 30, incl. the LTC
  drop-frame flag) and mapped to the matching ArtTimeCode type. Override it in the
  Rate box if you need to force one.
- **Offset (frames)** — default **+1**: an LTC frame is only fully readable at the
  end of the frame it labels, so +1 puts the output exactly on time. Adjustable
  ±10 frames for receiver-side alignment.
- **Freewheel** — on signal loss the bridge keeps generating consecutive frames at
  the measured rate for the configured number of frames (default 25), then goes
  silent and reports *Signal lost*.
- **Lock rule** — output only starts after two consecutive, range-valid, contiguous
  frames decode, so noise or a corrupted feed can never emit garbage timecode.
- **Device resilience** — if the audio device vanishes (USB unplugged, driver reset)
  the engine retries it every 2 s and freewheel covers the gap. Fatal events go to
  `timecodebridge.log` (capped at 1 MB, rotates mid-run).

## Show safety features

- **Art-Net discovery** — the bridge answers ArtPoll, so it appears by name
  ("Timecode Bridge") in console and network-scanner device lists.
- **Backup output** — optional second target IP (and interface): every frame is sent
  to both consoles simultaneously. Leave the Backup IP empty to disable.
- **Signal-loss alert** — Windows notification (optionally with sound) the moment
  timecode output stops or falls back to the generator; the tray dot turns red while
  output is dead.
- **Generator fallback** — when LTC dies beyond freewheel, the bridge keeps
  generating gapless timecode from the PC clock indefinitely until LTC returns
  (display shows GENERATOR in amber). If no LTC ever arrives, it free-runs from
  00:00:00:00 after 3 seconds.
- **Crash watchdog** — a companion process relaunches the bridge within ~5 seconds
  if it ever crashes. Clean quits (tray menu, Windows shutdown) do not trigger it.
- **Run at startup** — registers the app to launch at logon (straight to work, no
  interaction needed).
- **Settings lock** — LOCK SETTINGS freezes every control so nothing can be nudged
  mid-show; the lock state survives restarts.
- **Signal tolerance** — decoder is polarity-insensitive and copes with low level
  (−20 dB), DC offset, noise and ±10 % varispeed.

## Latency

| Stage | Typical |
|---|---|
| Audio input buffer | ASIO 1–10 ms · WASAPI ~10 ms · WDM ~10–20 ms |
| LTC decode + Art-Net send | < 0.1 ms (decoded the instant the sync word ends) |

With the default +1 frame offset the output lands within the audio-buffer time of
the true frame boundary — well under half a frame even via WDM.

## Engineering notes

- Decode path is allocation-free: driver callback → lock-free SPSC ring buffer →
  dedicated highest-priority decode thread → pre-connected UDP socket.
- Process runs at High priority with 1 ms timer resolution and sustained-low-latency GC.
- Single-instance guard; UI polls engine state at 30 Hz (no cross-thread marshalling).
- Reverse-play LTC is not decoded (by design — resyncs on forward play).

## Building from source

```powershell
dotnet test TimecodeBridge.sln -c Release        # 52 tests: decoder, DF math, Art-Net, engine, soak/fuzz, features
dotnet publish src\TimecodeBridge\TimecodeBridge.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

Layout: `src/TimecodeBridge` — app (`Ltc/` decoder + timecode math, `Audio/` inputs,
`ArtNet/` sender, `Core/` engine + ring buffer, `MainForm.cs` UI).
`tests/TimecodeBridge.Tests` — includes a reference LTC audio generator that
exercises the decoder across all rates, sample rates and signal degradation.
