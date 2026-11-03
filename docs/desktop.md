# Windows alarm

The Windows app listens to your ntfy topic. On an order, it opens a red window above everything else and loops an alarm sound until you answer.

You need ntfy as a channel. The app won't hear Telegram, Discord or Pushover alerts.

## Install

1. Download `SirenSetup.exe` from the [latest release](https://github.com/229amini/eldorado-siren/releases/latest).
2. Double-click it. You don't need admin rights or Python.
3. Enter your ntfy server address, topic and token. Siren tests the connection before it saves.

The app then runs in the background and starts when you sign in. Repeat these steps on each PC you use.

Windows may warn that it doesn't recognize the app, because the installer has no paid code-signing certificate. Click **More info**, then **Run anyway**. You can read the source in [`desktop/siren_desktop.py`](../desktop/siren_desktop.py) or build it yourself.

## Answer an alarm

Click **Got it**, or close the window. Both tell every device you saw it. Tapping **Got it** on your phone closes the window on your PC.

## Change settings

Press `Win + R` and run:

```
%LOCALAPPDATA%\Siren\Siren.exe --settings
```

To use your own sound, add a path to a `.wav` file as `"sound"` in `%LOCALAPPDATA%\Siren\config.json`, then restart the app.

## Uninstall

Open **Settings > Apps > Installed apps**, find **Siren**, and click **Uninstall**. That removes the app, its settings and its log.

## Troubleshooting

Siren writes a log to `%LOCALAPPDATA%\Siren\siren.log`.

- No `connected` line: your server address or token is wrong.
- `connected` but no alarm: the app's topic differs from `NTFY_TOPIC` on your server.

## Build it yourself

```powershell
powershell -ExecutionPolicy Bypass -File desktop\build.ps1
```

The result is `desktop\dist\SirenSetup.exe`. On a tagged release, GitHub Actions runs this same script and attaches the exe.
