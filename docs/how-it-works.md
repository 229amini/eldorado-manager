# How it works

```
                 every 30 s
 Eldorado API  <------------  Engine  ------>  ntfy      ----> phone, watch, Windows alarm
                                |     ------>  Telegram
                                |     ------>  Discord
                                |     ------>  Pushover
                                v
                            Dashboard  <-----  your browser
```

Eldorado's Seller API has no webhooks, so Siren polls it. Answers travel back the other way: the ntfy and Telegram channels hand the engine the order ids you acknowledged.

## Code map

| File | Job |
|---|---|
| `siren/engine.py` | The poll loop and every alarm decision. Start reading here |
| `siren/deadline.py` | Delivery windows, stages and repeat rates. Pure functions |
| `siren/eldorado.py` | API client and the `Order` and `Notice` models |
| `siren/channels.py` | Alert delivery |
| `siren/dashboard.py` | The web server |
| `siren/static/index.html` | The whole dashboard: one page, no build step |
| `siren/demo.py` | The fake account behind `siren demo` |
| `siren/config.py` | Environment variables |
| `desktop/siren_desktop.py` | The Windows app and installer in one file |

## One poll

`Engine.tick()` runs these steps in order:

1. Apply commands from the dashboard and answers from channels.
2. Read `Paid` orders, then start, repeat or end alarms.
3. Read `Disputed` orders and alert on new ones.
4. Read unread notifications and alert on new ones. The first poll records the backlog without alerting.
5. Refresh sales, at most every 5 minutes.
6. Check the dead-man switch.
7. Publish a new snapshot for the dashboard.

If a step raises an error, the loop logs it and tries again on the next poll. After 10 minutes of failures it alerts you.

## Deadlines

An order's deadline is its `deliveryStartedDate` plus the offer's `guaranteedDeliveryTime`. The API field `deliveryTime` looks like the answer, but it holds how long delivery took, and it stays empty until you deliver.

Stages depend on the share of the window left, so the same code handles a 5 minute order and a 100 day order.

## Alarm rules

Each open order has one alarm record:

| Field | Meaning |
|---|---|
| `first` | When Siren first saw the order |
| `last` | When Siren last alerted |
| `stage` | Highest stage reached |
| `stage_since` | When that stage started, for the repeat budget |
| `acked_stage` | Stage you answered at, or `None` |

The rules, in order:

1. A stage change alerts at once, unless you answered and the order hasn't turned critical.
2. An answer silences repeats. The alarm returns once if the order reaches critical or late after you answered.
3. Repeats stop when the stage budget runs out.
4. The record disappears when the order leaves `Paid`.

## API notes

Things the Swagger page doesn't tell you:

- `sold-order-data` returns CSV with a byte order mark, not JSON.
- Date fields can carry 7 fractional digits, more than Python's parser accepted before 3.11.
- Tokens last 15 minutes. `EldoradoClient` renews them 60 seconds early and retries once on a `401`.

## Design of the dashboard

The page has one idea: the room is the status light. You should be able to read your situation from across the room without reading any text, so the background carries the stage and the countdown fills the screen. The beam runs only while an alarm waits for an answer, which makes "someone needs to act" visible at a glance.

Big Shoulders Display sets the numbers. Chicago's signage inspired it, and its narrow digits let a countdown fill the width. Atkinson Hyperlegible, from the Braille Institute, sets everything else. Both ship with the app, so the dashboard works with no internet access.
