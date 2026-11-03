"""Command line: python -m siren {run,check,demo}."""

from __future__ import annotations

import argparse
import logging
import sys
from dataclasses import replace

from . import __version__, dashboard
from .channels import Alert, build_channels
from .config import Settings
from .demo import SCENES, ConsoleChannel, DemoApi
from .eldorado import EldoradoClient
from .engine import Engine, load_state, save_state

log = logging.getLogger("siren")


def client_for(settings: Settings) -> EldoradoClient:
    if not settings.client_id or not settings.client_secret:
        sys.exit("Set ELDORADO_CLIENT_ID and ELDORADO_CLIENT_SECRET first. See docs/getting-started.md.")
    return EldoradoClient(settings.base_url, settings.client_id, settings.client_secret)


def run(settings: Settings) -> None:
    api = client_for(settings)
    channels = build_channels(settings)
    if not channels:
        log.warning("no alert channel configured: Siren will only update the dashboard")
    engine = Engine(api, channels, settings, load_state(settings.state_file))
    if settings.dashboard_port:
        dashboard.start(engine, settings.dashboard_host, settings.dashboard_port, settings.dashboard_token)
    engine.run(save=lambda state: save_state(settings.state_file, state))


def check(settings: Settings) -> None:
    """Touch every dependency once and report. Reads your account, changes nothing."""
    api = client_for(settings)
    print(f"Open orders:       {len(api.orders('Paid'))}")
    print(f"Disputed orders:   {len(api.orders('Disputed'))}")
    print(f"Unread notices:    {len(api.unread_notices())}")
    print(f"Sales in 7 days:   {len(api.sales_since(7 * 86400))}")
    channels = build_channels(settings)
    if not channels:
        print("Channels:          none configured")
    failed = False
    for channel in channels:
        try:
            channel.send(Alert("system", 5, "Siren test", "If you can read this, this channel works."))
            print(f"{channel.name + ':':<19}test sent")
        except Exception as error:
            failed = True
            print(f"{channel.name + ':':<19}FAILED: {error}")
    sys.exit(1 if failed else 0)


def demo(settings: Settings, scene: str, host: str, port: int) -> None:
    engine = Engine(DemoApi(scene), [ConsoleChannel()], settings)
    dashboard.start(engine, host, port, "")
    print(f"Demo running: open http://127.0.0.1:{port}  (scene: {scene}, Ctrl+C to stop)")
    engine.run()


def main() -> None:
    parser = argparse.ArgumentParser(prog="siren", description="Order alarms for Eldorado.gg sellers.")
    parser.add_argument("--version", action="version", version=__version__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("run", help="watch your account until stopped")
    commands.add_parser("check", help="test your credentials and send a test alert to each channel")
    demo_parser = commands.add_parser("demo", help="run the dashboard against a fake account")
    demo_parser.add_argument("--scene", choices=sorted(SCENES), default="hurry")
    demo_parser.add_argument("--host", default="127.0.0.1")
    demo_parser.add_argument("--port", type=int, default=8787)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    settings = Settings.from_env()
    if args.command == "run":
        run(settings)
    elif args.command == "check":
        check(settings)
    else:
        demo(replace(settings, poll_seconds=3), args.scene, args.host, args.port)


if __name__ == "__main__":
    main()
