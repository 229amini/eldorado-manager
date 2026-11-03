# Configuration

Siren reads environment variables. With Docker, put them in `.env`.

## Eldorado

| Variable | Default | Meaning |
|---|---|---|
| `ELDORADO_CLIENT_ID` | | Required |
| `ELDORADO_CLIENT_SECRET` | | Required |
| `ELDORADO_BASE_URL` | `https://www.eldorado.gg` | API address |

## Timing

| Variable | Default | Meaning |
|---|---|---|
| `POLL_SECONDS` | `30` | Time between checks. This sets how late an alarm can start, and it caps how fast repeats go |
| `ALARM_REPEAT_SECONDS` | `60` | Repeat rate in the calm stage. Hurry runs at half this, critical and late at a quarter |
| `ALARM_STAGE_BUDGET_MINUTES` | `30` | Stop repeating after this long in one stage. The next stage starts a fresh budget |
| `ALARM_HURRY_AT` | `0.5` | Share of the delivery window left when hurry starts |
| `ALARM_CRITICAL_AT` | `0.2` | Share left when critical starts |
| `UNHEALTHY_AFTER_SECONDS` | `600` | Send the "can't reach Eldorado" alert after this long |

To hear critical repeats at the full 15 second rate, set `POLL_SECONDS=15`.

## Dead-man switch

| Variable | Default | Meaning |
|---|---|---|
| `DEADMAN_ENABLED` | `false` | Set to `true` to allow Siren to take you offline |
| `DEADMAN_MINUTES` | `10` | How long an alarm can go unanswered first |

When it fires, Siren calls `switchOffline` once and alerts you. Siren never goes online by itself: click **Go online** on the dashboard, or switch on Eldorado.

Going offline doesn't cancel or deliver the order that triggered it.

## Dashboard

| Variable | Default | Meaning |
|---|---|---|
| `DASHBOARD_HOST_PORT` | `8787` | With Docker: the port on your machine |
| `DASHBOARD_PORT` | `8787` | Without Docker: the port to listen on. `0` turns the dashboard off |
| `DASHBOARD_HOST` | `0.0.0.0` | Without Docker: the bind address. Use `127.0.0.1` on a public server |
| `DASHBOARD_TOKEN` | | Password for the dashboard. Set this before you expose the port |

## Channels

| Variable | Used by |
|---|---|
| `NTFY_URL`, `NTFY_TOPIC`, `NTFY_TOKEN`, `NTFY_PUBLIC_URL` | ntfy |
| `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID` | Telegram |
| `DISCORD_WEBHOOK_URL` | Discord |
| `PUSHOVER_APP_TOKEN`, `PUSHOVER_USER_KEY` | Pushover |

[Channels](channels.md) shows where to get each value.

## Docker build

| Variable | Default | Meaning |
|---|---|---|
| `PYTHON_IMAGE` | `python:3.12-alpine` | Base image |
| `NTFY_IMAGE` | `binwiederhier/ntfy:v2.37.0` | ntfy image |
| `STATE_FILE` | `/data/state.json` | Where Siren remembers alarms between restarts |
