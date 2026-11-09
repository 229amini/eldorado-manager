# Channels

Eldorado Manager sends to each channel you configure in `.env`. You can run several at once.

| Channel | Cost | Repeats alarms | Got it button |
|---|---|---|---|
| ntfy | Free | Yes | Yes |
| Telegram | Free | Yes | Yes |
| Pushover | One-time fee | Yes | No |
| Discord | Free | No, first alert and stage changes only | No |

A channel without a button still stops ringing when you answer on another channel or on the dashboard, or when you deliver the order.

## ntfy

ntfy gives you the loudest phone alert, and the Windows alarm depends on it.

```sh
NTFY_URL=https://ntfy.sh
NTFY_TOPIC=a-long-random-name
NTFY_TOKEN=            # only for a server with login
```

Eldorado Manager also uses a second topic, `<NTFY_TOPIC>-ack`, for answers. If your server has access control, grant the token read and write on both. A wildcard such as `eldorado*` covers them.

If your phone reaches ntfy at a different address than Eldorado Manager does, set `NTFY_PUBLIC_URL` to the address the phone uses.

## Telegram

1. Message [@BotFather](https://t.me/BotFather), send `/newbot`, and copy the token.
2. Send any message to your new bot.
3. Open `https://api.telegram.org/bot<TOKEN>/getUpdates` and copy `chat.id`.

```sh
TELEGRAM_BOT_TOKEN=123456:abc
TELEGRAM_CHAT_ID=987654
```

Eldorado Manager accepts button taps from that chat alone.

## Discord

In your server: **Server Settings > Integrations > Webhooks > New Webhook**. Copy the URL.

```sh
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
```

Top-level alerts mention `@here`. Eldorado Manager skips repeats on Discord so your channel stays readable.

## Pushover

Create an application at [pushover.net](https://pushover.net/apps/build). Your user key is on the Pushover home page.

```sh
PUSHOVER_APP_TOKEN=...
PUSHOVER_USER_KEY=...
```

## Webhook

Send alerts to your own alert server as JSON. Eldorado Manager posts when an order opens, when it reaches its last minutes and when it closes. Your server handles any repeats.

```sh
WEBHOOK_URL=https://alerts.example.com/events
WEBHOOK_TOKEN=...
```

An order alert looks like this. `key` is the order id, and `ring` is `true` when an order you already heard about needs you again:

```json
{"title": "New order", "body": "Coins, 6.30 USD", "link": "https://www.eldorado.gg/order/...", "key": "...",
 "deadline": 1793976194.0, "window": 1200, "level": 5, "style": "call", "ring": false}
```

When the order closes, Eldorado Manager posts `{"key": "...", "resolved": true}`.

## Write your own channel

Add a class to `eldorado_manager/channels.py`, then register it in `build_channels()`. This one posts to Slack:

```python
class Slack(Channel):
    name = "slack"
    wants_repeats = False

    def __init__(self, webhook_url, transport=request):
        self.webhook_url = webhook_url
        self.transport = transport

    def send(self, alert):
        self.transport("POST", self.webhook_url, json_body={"text": f"*{alert.title}*\n{alert.body}"})
```

Each `Alert` carries:

| Field | Meaning |
|---|---|
| `kind` | `order`, `dispute`, `notice` or `system` |
| `level` | 1 to 5. Orders use 5 |
| `title`, `body` | Text ready to show |
| `url` | Link to the order, or `None` |
| `ack_id` | Order id if this alert can take an answer |
| `repeat` | `True` when this repeats an earlier alert |

If your channel can take answers, add `poll_acks()` and return the order ids people acknowledged. Eldorado Manager calls it on each check. See `Telegram` for a working example.

Pass `transport` as an argument, as the example does, so your tests can run without a network. `tests/test_client.py` shows how.
