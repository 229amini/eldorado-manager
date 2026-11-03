"""Delivery deadlines and the urgency ladder.

Eldorado sells each offer with a guaranteed delivery time. An alarm for a 20 minute order and an
alarm for a 7 day order should not behave the same, so Siren measures urgency as the share of the
delivery window still left, and speeds up as that share shrinks.
"""

from __future__ import annotations

from datetime import datetime
from enum import IntEnum

MINUTE, HOUR, DAY = 60, 3600, 86400

# Values of the GuaranteedDeliveryTime enum in the Seller API.
GUARANTEE_SECONDS = {
    "Instant": 5 * MINUTE, "Minute5": 5 * MINUTE, "Minute20": 20 * MINUTE,
    "Hour1": HOUR, "Hour2": 2 * HOUR, "Hour3": 3 * HOUR, "Hour5": 5 * HOUR,
    "Hour8": 8 * HOUR, "Hour12": 12 * HOUR,
    "Day1": DAY, "Day2": 2 * DAY, "Day3": 3 * DAY, "Day5": 5 * DAY, "Day7": 7 * DAY,
    "Day10": 10 * DAY, "Day14": 14 * DAY, "Day28": 28 * DAY, "Day45": 45 * DAY,
    "Day60": 60 * DAY, "Day80": 80 * DAY, "Day100": 100 * DAY,
}


class Stage(IntEnum):
    CALM = 0
    HURRY = 1
    CRITICAL = 2
    LATE = 3


def parse_time(value: str | None) -> float | None:
    """Return a Unix timestamp for an API date, which may carry 7 fractional digits."""
    if not value:
        return None
    head, dot, tail = value.partition(".")
    if dot:  # trim to microseconds so every supported Python accepts it
        digits = "".join(c for c in tail if c.isdigit())
        value = f"{head}.{digits[:6]}{tail[len(digits):]}"
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def stage_for(remaining: float | None, window: float | None, hurry_at=0.5, critical_at=0.2) -> Stage:
    if remaining is None or not window:
        return Stage.CALM
    if remaining <= 0:
        return Stage.LATE
    share = remaining / window
    if share <= critical_at:
        return Stage.CRITICAL
    if share <= hurry_at:
        return Stage.HURRY
    return Stage.CALM


def repeat_interval(stage: Stage, base_seconds: int) -> float:
    return {Stage.CALM: base_seconds, Stage.HURRY: base_seconds / 2}.get(stage, base_seconds / 4)


def format_remaining(seconds: float) -> str:
    seconds = int(abs(seconds))
    days, rest = divmod(seconds, DAY)
    hours, rest = divmod(rest, HOUR)
    minutes = rest // MINUTE
    if days:
        return f"{days}d {hours}h"
    if hours:
        return f"{hours}h {minutes}m"
    return f"{minutes}m {seconds % MINUTE:02d}s"
