"""Settings, read once from the environment. docs/configuration.md lists every variable."""

from __future__ import annotations

import os
from dataclasses import dataclass


def _bool(value: str) -> bool:
    return value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    client_id: str = ""
    client_secret: str = ""
    base_url: str = "https://www.eldorado.gg"

    poll_seconds: int = 30
    repeat_seconds: int = 60          # alarm cadence in the calm stage; later stages go faster
    stage_budget_minutes: int = 30    # stop repeating after this long in one stage
    hurry_at: float = 0.5             # share of the delivery window left when "hurry" starts
    critical_at: float = 0.2          # same, for "critical"
    unhealthy_after_seconds: int = 600

    deadman_enabled: bool = False
    deadman_minutes: int = 10

    ntfy_url: str = ""
    ntfy_public_url: str = ""
    ntfy_topic: str = "eldorado"
    ntfy_token: str = ""
    telegram_bot_token: str = ""
    telegram_chat_id: str = ""
    discord_webhook_url: str = ""
    pushover_app_token: str = ""
    pushover_user_key: str = ""
    webhook_url: str = ""
    webhook_token: str = ""

    dashboard_host: str = "0.0.0.0"
    dashboard_port: int = 8787
    dashboard_token: str = ""

    state_file: str = "/data/state.json"

    market_enabled: bool = True
    market_pass_seconds: int = 180    # standings pass; the site order is walked on every 5th
    market_sweep_minutes: int = 30    # listing sweep for the market watch
    market_file: str = "/data/market.json"
    ledger_file: str = "/data/ledger.json"

    @classmethod
    def from_env(cls, env=None) -> "Settings":
        env = os.environ if env is None else env

        def get(name, default=""):
            return env.get(name, default).strip()

        state_file = get("STATE_FILE", "/data/state.json")
        return cls(
            client_id=get("ELDORADO_CLIENT_ID"),
            client_secret=get("ELDORADO_CLIENT_SECRET"),
            base_url=get("ELDORADO_BASE_URL", cls.base_url).rstrip("/"),
            poll_seconds=int(get("POLL_SECONDS", "30")),
            repeat_seconds=int(get("ALARM_REPEAT_SECONDS", "60")),
            stage_budget_minutes=int(get("ALARM_STAGE_BUDGET_MINUTES", "30")),
            hurry_at=float(get("ALARM_HURRY_AT", "0.5")),
            critical_at=float(get("ALARM_CRITICAL_AT", "0.2")),
            unhealthy_after_seconds=int(get("UNHEALTHY_AFTER_SECONDS", "600")),
            deadman_enabled=_bool(get("DEADMAN_ENABLED", "false")),
            deadman_minutes=int(get("DEADMAN_MINUTES", "10")),
            ntfy_url=get("NTFY_URL").rstrip("/"),
            ntfy_public_url=(get("NTFY_PUBLIC_URL") or get("NTFY_URL")).rstrip("/"),
            ntfy_topic=get("NTFY_TOPIC", "eldorado"),
            ntfy_token=get("NTFY_TOKEN"),
            telegram_bot_token=get("TELEGRAM_BOT_TOKEN"),
            telegram_chat_id=get("TELEGRAM_CHAT_ID"),
            discord_webhook_url=get("DISCORD_WEBHOOK_URL"),
            pushover_app_token=get("PUSHOVER_APP_TOKEN"),
            pushover_user_key=get("PUSHOVER_USER_KEY"),
            webhook_url=get("WEBHOOK_URL"),
            webhook_token=get("WEBHOOK_TOKEN"),
            dashboard_host=get("DASHBOARD_HOST", "0.0.0.0"),
            dashboard_port=int(get("DASHBOARD_PORT", "8787")),
            dashboard_token=get("DASHBOARD_TOKEN"),
            state_file=state_file,
            market_enabled=_bool(get("MARKET_ENABLED", "true")),
            market_pass_seconds=int(get("MARKET_PASS_SECONDS", "180")),
            market_sweep_minutes=int(get("MARKET_SWEEP_MINUTES", "30")),
            market_file=get("MARKET_FILE") or os.path.join(os.path.dirname(state_file), "market.json"),
            ledger_file=get("LEDGER_FILE") or os.path.join(os.path.dirname(state_file), "ledger.json"),
        )
