"""Siren for Windows: a loud desktop alarm that listens to your ntfy topic.

An order alarm opens an always-on-top window and loops a sound until you click "Got it" here or
tap "Got it" on your phone. Either answer closes the alarm everywhere.

build.ps1 freezes this file into SirenSetup.exe. That one exe is both installer and app. Run from
anywhere, it installs itself for the current user (no admin rights, no Python). Run from the
install folder, it is the alarm. It also accepts --settings, --uninstall and --silent.
"""

import ctypes
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
import urllib.error
import urllib.request
import webbrowser

try:
    import winsound
except ImportError:  # other systems still get the window, without the looping sound
    winsound = None

APP = "Siren"
FROZEN = getattr(sys, "frozen", False)
HOME = os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.local/share"), APP)
INSTALL_EXE = os.path.join(HOME, APP + ".exe")
CONFIG_PATH = os.path.join(HOME, "config.json")
LOG_PATH = os.path.join(HOME, "siren.log")
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
UNINSTALL_KEY = rf"Software\Microsoft\Windows\CurrentVersion\Uninstall\{APP}"
DEFAULT_SOUND = r"C:\Windows\Media\Alarm01.wav"
RED, INK, PANEL = "#D4161C", "#FFF6F2", "#14285C"


def log(*parts):
    os.makedirs(HOME, exist_ok=True)
    with open(LOG_PATH, "a", encoding="utf-8") as file:
        file.write(time.strftime("%Y-%m-%d %H:%M:%S ") + " ".join(map(str, parts)) + "\n")


def load_config():
    try:
        with open(CONFIG_PATH, encoding="utf-8") as file:
            return json.load(file)
    except (FileNotFoundError, ValueError):
        return None


def save_config(config):
    os.makedirs(HOME, exist_ok=True)
    with open(CONFIG_PATH, "w", encoding="utf-8") as file:
        json.dump(config, file, indent=2)


def auth_headers(config):
    return {"Authorization": f"Bearer {config['token']}"} if config.get("token") else {}


def test_connection(config):
    """Return an error message, or None when the topic is readable."""
    url = f"{config['url'].rstrip('/')}/{config['topic']}/json?poll=1"
    try:
        urllib.request.urlopen(urllib.request.Request(url, headers=auth_headers(config)), timeout=15).read()
    except urllib.error.HTTPError as error:
        return f"The server answered {error.code}. Check the topic and token."
    except Exception as error:
        return f"Siren can't reach that server: {error}"
    return None


# ---------- install ----------

def message(text):
    ctypes.windll.user32.MessageBoxW(None, text, APP, 0x40)


def launch(*args):
    # A frozen child must not inherit this process's unpacked bundle folder.
    env = dict(os.environ, PYINSTALLER_RESET_ENVIRONMENT="1")
    subprocess.Popen([INSTALL_EXE, *args], env=env, cwd=HOME, close_fds=True,
                     creationflags=subprocess.DETACHED_PROCESS)


def stop_installed(keep_self=False):
    command = ["taskkill", "/F", "/IM", APP + ".exe"]
    if keep_self:
        command += ["/FI", f"PID ne {os.getpid()}"]
    subprocess.run(command, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)


def install():
    import winreg
    stop_installed()  # setup runs as SirenSetup.exe, so this only hits an older copy
    time.sleep(1)
    os.makedirs(HOME, exist_ok=True)
    shutil.copy2(sys.executable, INSTALL_EXE)
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        winreg.SetValueEx(key, APP, 0, winreg.REG_SZ, f'"{INSTALL_EXE}"')
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY) as key:
        for name, value in (("DisplayName", APP), ("Publisher", "eldorado-siren"), ("DisplayIcon", INSTALL_EXE),
                            ("InstallLocation", HOME), ("UninstallString", f'"{INSTALL_EXE}" --uninstall')):
            winreg.SetValueEx(key, name, 0, winreg.REG_SZ, value)
        winreg.SetValueEx(key, "NoModify", 0, winreg.REG_DWORD, 1)
        winreg.SetValueEx(key, "NoRepair", 0, winreg.REG_DWORD, 1)
    launch()
    log("installed", INSTALL_EXE)
    if "--silent" not in sys.argv and load_config():
        message("Siren is installed and running in the background.\nIt starts when you sign in to Windows.")


def uninstall():
    import winreg
    stop_installed(keep_self=True)
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
        try:
            winreg.DeleteValue(key, APP)
        except FileNotFoundError:
            pass
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY)
    except FileNotFoundError:
        pass
    if "--silent" not in sys.argv:
        message("Siren has been removed.")
    # A running exe can't delete itself, so a detached shell finishes after this process exits.
    subprocess.Popen(f'cmd /c ping -n 4 127.0.0.1 >nul & rmdir /s /q "{HOME}" & rmdir /s /q "{sys._MEIPASS}"',
                     creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NO_WINDOW, cwd=os.environ["TEMP"])


def already_running():
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.restype = ctypes.c_void_p
    already_running.handle = kernel.CreateMutexW(None, False, "Local\\" + APP)  # held until exit
    return ctypes.get_last_error() == 183  # ERROR_ALREADY_EXISTS


# ---------- settings window ----------

def settings_window(existing=None):
    """Ask for the ntfy details. Returns the saved config, or None if the user closed the window."""
    existing = existing or {}
    root = tk.Tk()
    root.title("Siren settings")
    root.configure(bg=PANEL, padx=28, pady=22)
    root.resizable(False, False)
    result = {}
    fields = {}
    tk.Label(root, text="Connect Siren to your alerts", fg=INK, bg=PANEL,
             font=("Segoe UI", 16, "bold")).grid(row=0, column=0, columnspan=2, sticky="w", pady=(0, 14))
    rows = (("url", "ntfy server address", existing.get("url", "https://ntfy.sh"), ""),
            ("topic", "Topic", existing.get("topic", "eldorado"), ""),
            ("token", "Access token (optional)", existing.get("token", ""), "*"))
    for index, (key, label, value, mask) in enumerate(rows, start=1):
        tk.Label(root, text=label, fg=INK, bg=PANEL, font=("Segoe UI", 11)).grid(row=index, column=0, sticky="w", pady=5)
        entry = tk.Entry(root, width=38, show=mask, font=("Segoe UI", 11))
        entry.insert(0, value)
        entry.grid(row=index, column=1, padx=(14, 0), pady=5)
        fields[key] = entry
    status = tk.Label(root, text="", fg="#FFD3CC", bg=PANEL, wraplength=430, justify="left", font=("Segoe UI", 10))
    status.grid(row=4, column=0, columnspan=2, sticky="w", pady=(8, 0))

    def submit():
        config = {key: entry.get().strip() for key, entry in fields.items()}
        config["url"] = config["url"].rstrip("/")
        config["ack_topic"] = f"{config['topic']}-ack"
        config["sound"] = existing.get("sound", DEFAULT_SOUND)
        status.config(text="Testing the connection")
        root.update()
        error = test_connection(config)
        if error:
            status.config(text=error)
            return
        save_config(config)
        result["config"] = config
        root.destroy()

    tk.Button(root, text="Save and start", command=submit, font=("Segoe UI", 11, "bold")).grid(
        row=5, column=1, sticky="e", pady=(14, 0))
    root.bind("<Return>", lambda _event: submit())
    root.mainloop()
    return result.get("config")


# ---------- alarm ----------

class Listener(threading.Thread):
    """Streams both topics forever and resumes from the last message id after a drop."""

    def __init__(self, config, events):
        super().__init__(daemon=True)
        self.config, self.events = config, events

    def run(self):
        config, since = self.config, None
        base = f"{config['url']}/{config['topic']},{config['ack_topic']}/json"
        while True:
            url = base + (f"?since={since}" if since else "")
            try:
                request = urllib.request.Request(url, headers=auth_headers(config))
                with urllib.request.urlopen(request, timeout=120) as stream:  # ntfy keepalive is ~45 s
                    log("connected")
                    for line in stream:
                        event = json.loads(line)
                        if event.get("event") == "message":
                            since = event["id"]
                            self.events.put(event)
            except Exception as error:
                log("stream error:", repr(error))
                time.sleep(5)


def ack_id_of(event, ack_topic):
    for action in event.get("actions") or []:
        if action.get("action") == "http" and action.get("url", "").rstrip("/").endswith("/" + ack_topic):
            return action.get("body")
    return None


class App:
    def __init__(self, config):
        self.config = config
        self.events = queue.Queue()
        self.root = tk.Tk()
        self.root.withdraw()
        self.alarms = {}        # order id -> window
        self.answered = set()   # repeats can arrive for one poll after an ack; ignore those
        self.playing = False

    def run(self):
        Listener(self.config, self.events).start()
        self.root.after(200, self.pump)
        self.root.mainloop()

    def pump(self):
        try:
            while True:
                self.handle(self.events.get_nowait())
        except queue.Empty:
            pass
        self.root.after(200, self.pump)

    def handle(self, event):
        if event.get("topic") == self.config["ack_topic"]:
            self.close((event.get("message") or "").strip())
            return
        order_id = ack_id_of(event, self.config["ack_topic"])
        if order_id:
            if order_id in self.answered:
                return
            if order_id in self.alarms:
                self.alarms[order_id].lift()
                return
            self.alarms[order_id] = self.window(event, order_id)
            log("alarm opened:", order_id)
            self.update_sound()
        elif (event.get("priority") or 3) >= 3:
            self.window(event)
            if winsound and (event.get("priority") or 3) >= 4:
                winsound.MessageBeep(winsound.MB_ICONEXCLAMATION)

    def window(self, event, order_id=None):
        window = tk.Toplevel(self.root)
        background = RED if order_id else PANEL
        window.title(event.get("title") or APP)
        window.attributes("-topmost", True)
        window.configure(bg=background, padx=26, pady=20)
        tk.Label(window, text=event.get("title") or APP, fg=INK, bg=background,
                 font=("Segoe UI", 22, "bold")).pack(anchor="w")
        tk.Label(window, text=event.get("message") or "", fg=INK, bg=background, justify="left",
                 wraplength=540, font=("Segoe UI", 13)).pack(anchor="w", pady=(6, 16))
        row = tk.Frame(window, bg=background)
        row.pack(anchor="e")
        if event.get("click"):
            tk.Button(row, text="Open order", font=("Segoe UI", 12),
                      command=lambda: webbrowser.open(event["click"])).pack(side="left", padx=6)
        if order_id:
            tk.Button(row, text="Got it", font=("Segoe UI", 12, "bold"),
                      command=lambda: self.answer(order_id)).pack(side="left", padx=6)
            window.protocol("WM_DELETE_WINDOW", lambda: self.answer(order_id))
        else:
            tk.Button(row, text="Close", font=("Segoe UI", 12), command=window.destroy).pack(side="left", padx=6)
            if (event.get("priority") or 3) <= 3:
                window.after(20000, window.destroy)
        window.lift()
        window.focus_force()
        return window

    def answer(self, order_id):
        def publish():
            try:
                request = urllib.request.Request(f"{self.config['url']}/{self.config['ack_topic']}",
                                                 data=order_id.encode(), headers=auth_headers(self.config),
                                                 method="POST")
                urllib.request.urlopen(request, timeout=20).read()
            except Exception as error:
                log("ack failed:", repr(error))
        threading.Thread(target=publish, daemon=True).start()
        self.close(order_id)

    def close(self, order_id):
        self.answered.add(order_id)
        window = self.alarms.pop(order_id, None)
        if window is not None:
            window.destroy()
            log("alarm closed:", order_id)
        self.update_sound()

    def update_sound(self):
        if not winsound:
            return
        if self.alarms and not self.playing:
            flags = winsound.SND_ASYNC | winsound.SND_LOOP
            try:
                winsound.PlaySound(self.config.get("sound") or DEFAULT_SOUND, winsound.SND_FILENAME | flags)
            except RuntimeError:
                winsound.PlaySound("SystemHand", winsound.SND_ALIAS | flags)
            self.playing = True
        elif not self.alarms and self.playing:
            winsound.PlaySound(None, 0)
            self.playing = False


def main():
    if FROZEN and "--uninstall" in sys.argv:
        uninstall()
    elif FROZEN and os.path.normcase(sys.executable) != os.path.normcase(INSTALL_EXE):
        install()
    elif "--settings" in sys.argv:
        if settings_window(load_config()) and FROZEN:
            stop_installed(keep_self=True)
            launch()
    elif not (FROZEN and already_running()):
        config = load_config() or settings_window()
        if config:
            App(config).run()


if __name__ == "__main__":
    main()
