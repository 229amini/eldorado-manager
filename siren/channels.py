"""Where alerts go. To add a channel, subclass Channel and register it in build_channels().

A channel implements send(). If its users can answer an alert, it also implements poll_acks()
and returns the order ids they acknowledged. docs/channels.md walks through an example.
"""

from __future__ import annotations

import json
import time
import urllib.parse
from dataclasses import dataclass

from .config import Settings
from .net import request

ACK_LABEL = "Got it"
OPEN_LABEL = "Open"


@dataclass(frozen=True)
class Alert:
    kind: str                 # order, dispute, notice, system
    level: int                # 1 (quiet) to 5 (wake me up)
    title: str
    body: str
    url: str | None = None
    ack_id: str | None = None
    repeat: bool = False      # True when this alert repeats an earlier one
    deadline: float | None = None   # Unix seconds, for the Windows countdown
    window: float | None = None     # delivery window in seconds


class Channel:
    name = "channel"
    wants_repeats = True      # False for chat rooms, where repeats turn into spam

    def send(self, alert: Alert) -> None:
        raise NotImplementedError

    def poll_acks(self) -> set[str]:
        return set()


class Ntfy(Channel):
    """Push to the ntfy app. The "Got it" button publishes the order id to <topic>-ack."""

    name = "ntfy"
    TAGS = {"order": ["rotating_light"], "dispute": ["warning"], "notice": ["bell"], "system": ["robot"]}

    def __init__(self, url, public_url, topic, token, transport=request):
        self.url, self.public_url, self.topic = url, public_url, topic
        self.ack_topic = f"{topic}-ack"
        self.auth = {"Authorization": f"Bearer {token}"} if token else {}
        self.transport = transport
        self.since = str(int(time.time()))

    def send(self, alert):
        message = {"topic": self.topic, "title": alert.title, "message": alert.body,
                   "priority": alert.level, "tags": self.TAGS.get(alert.kind, [])}
        actions = []
        if alert.ack_id:
            headers = dict(self.auth)
            if alert.deadline is not None and alert.window:   # the Windows app reads its countdown from here
                headers.update({"X-Siren-Deadline": str(alert.deadline), "X-Siren-Window": str(alert.window)})
            actions.append({"action": "http", "label": ACK_LABEL, "method": "POST", "clear": True,
                            "url": f"{self.public_url}/{self.ack_topic}", "body": alert.ack_id,
                            "headers": headers})
        if alert.url:   # a button, so tapping the alert to silence it doesn't open a browser
            actions.append({"action": "view", "label": OPEN_LABEL, "url": alert.url})
        if actions:
            message["actions"] = actions
        self.transport("POST", self.url, headers=self.auth, json_body=message)

    def poll_acks(self):
        raw = self.transport("GET", f"{self.url}/{self.ack_topic}/json?poll=1&since={self.since}",
                             headers=self.auth)
        acks = set()
        for line in filter(None, raw.splitlines()):
            event = json.loads(line)
            if event.get("event") == "message":
                acks.add(event.get("message", "").strip())
                self.since = event["id"]
        return acks


class Telegram(Channel):
    """Bot messages with an inline "Got it" button. Siren reads button taps with getUpdates."""

    name = "telegram"

    def __init__(self, token, chat_id, transport=request):
        self.api = f"https://api.telegram.org/bot{token}"
        self.chat_id = str(chat_id)
        self.transport = transport
        self.offset = 0

    def send(self, alert):
        message = {"chat_id": self.chat_id, "text": f"{alert.title}\n{alert.body}",
                   "disable_notification": alert.level < 3}
        buttons = []
        if alert.ack_id:
            buttons.append({"text": ACK_LABEL, "callback_data": f"ack:{alert.ack_id}"})
        if alert.url:
            buttons.append({"text": "Open", "url": alert.url})
        if buttons:
            message["reply_markup"] = {"inline_keyboard": [buttons]}
        self.transport("POST", f"{self.api}/sendMessage", json_body=message)

    def poll_acks(self):
        query = urllib.parse.urlencode({"offset": self.offset, "timeout": 0,
                                        "allowed_updates": '["callback_query"]'})
        reply = json.loads(self.transport("GET", f"{self.api}/getUpdates?{query}"))
        acks = set()
        for update in reply.get("result", []):
            self.offset = update["update_id"] + 1
            tap = update.get("callback_query") or {}
            chat = str(((tap.get("message") or {}).get("chat") or {}).get("id"))
            if chat == self.chat_id and (tap.get("data") or "").startswith("ack:"):
                acks.add(tap["data"][4:])
                self.transport("POST", f"{self.api}/answerCallbackQuery",
                               json_body={"callback_query_id": tap["id"], "text": "Alarm stopped"})
        return acks


class Discord(Channel):
    """Webhook embeds. A webhook can't take button taps, so acks come from other channels."""

    name = "discord"
    wants_repeats = False
    COLORS = {5: 0xD4161C, 4: 0xF2A900, 3: 0x3D5AFE}

    def __init__(self, webhook_url, transport=request):
        self.webhook_url = webhook_url
        self.transport = transport

    def send(self, alert):
        embed = {"title": alert.title, "description": alert.body, "color": self.COLORS.get(alert.level, 0x8A93A6)}
        if alert.url:
            embed["url"] = alert.url
        message = {"username": "Siren", "embeds": [embed]}
        if alert.level >= 5:
            message["content"] = "@here"
        self.transport("POST", self.webhook_url, json_body=message)


class Pushover(Channel):
    name = "pushover"
    PRIORITY = {5: 1, 4: 1, 3: 0, 2: -1, 1: -2}

    def __init__(self, app_token, user_key, transport=request):
        self.app_token, self.user_key = app_token, user_key
        self.transport = transport

    def send(self, alert):
        fields = {"token": self.app_token, "user": self.user_key, "title": alert.title,
                  "message": alert.body, "priority": self.PRIORITY.get(alert.level, 0)}
        if alert.level >= 5:
            fields["sound"] = "siren"
        if alert.url:
            fields["url"] = alert.url
        self.transport("POST", "https://api.pushover.net/1/messages.json",
                       headers={"Content-Type": "application/x-www-form-urlencoded"},
                       data=urllib.parse.urlencode(fields))


def build_channels(settings: Settings) -> list[Channel]:
    channels: list[Channel] = []
    if settings.ntfy_url:
        channels.append(Ntfy(settings.ntfy_url, settings.ntfy_public_url, settings.ntfy_topic, settings.ntfy_token))
    if settings.telegram_bot_token and settings.telegram_chat_id:
        channels.append(Telegram(settings.telegram_bot_token, settings.telegram_chat_id))
    if settings.discord_webhook_url:
        channels.append(Discord(settings.discord_webhook_url))
    if settings.pushover_app_token and settings.pushover_user_key:
        channels.append(Pushover(settings.pushover_app_token, settings.pushover_user_key))
    return channels
