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

Eldorado's Seller API has no webhooks, so Eldorado Manager polls it. Answers travel back the other way: the ntfy and Telegram channels hand the engine the order ids you acknowledged.

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
| `desktop/Siren/` | The Windows app and installer in one exe. See [below](#windows-app) |

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
| `first` | When Eldorado Manager first saw the order |
| `last` | When Eldorado Manager last alerted |
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
- `GET currency-management/me/offers` takes its filter as a JSON body, and `augmentedGameFilter` needs a `gameId` and a `category`. There is no call for all your currency offers, so the dashboard asks once for each game your item offers are in. A seller with currency offers only won't see them yet.
- The public currency listing ignores the price sort, and it mixes offers priced per 1K and per 1M. Eldorado Manager walks all of it and compares only offers with your unit.

- `sold-order-data` lists `Completed` orders only, about 5 days after the sale, and dates them by completion. The Windows app counts sales from `orders/me/seller/orders` by the day the buyer paid, and takes only cleared net income and fees from the CSV.
- Eldorado's `orderCounts` field comes back empty on your offers and on public ones, so no call tells you how much another seller sold.
- Prices take 2 decimals, or up to 5 below 0.01. Anything else gets a `400`.
- The bulk price call puts one price on every offer it matches. The Windows app changes prices one offer at a time for that reason.

## Windows app

C# on .NET Framework 4.8, which ships with Windows, so the exe needs no runtime. It runs as two processes:

| Process | Started with | Job |
|---|---|---|
| Alarm | `--background`, at sign-in | Listens to ntfy, shows the island and the red screen, runs the market watch |
| Dashboard | `--dashboard` | Reads your shop and sends your edits |

| File | Job |
|---|---|
| `Program.cs`, `Installer.cs` | Flags, and the exe installing itself |
| `AlarmWindow.xaml.cs`, `TakeoverWindow.xaml.cs`, `Listener.cs` | The alarm |
| `DashboardWindow.xaml.cs` | The dashboard's loop and every write |
| `Market.cs` | The Eldorado API client |
| `Insights.cs` | The Insights tab |
| `MarketWatch.cs` | Counts falls in other sellers' stock |
| `Web/` | The dashboard page, shown in WebView2. No build step |

The installer copies one file, so the page, the sounds and the WebView2 libraries ride inside the exe. `WebHost.cs` unpacks what has to exist on disk.

## Design of the dashboard

The page has one idea: the room is the status light. You should be able to read your situation from across the room without reading any text, so the background carries the stage and the countdown fills the screen. The beam runs only while an alarm waits for an answer, which makes "someone needs to act" visible at a glance.

Big Shoulders Display sets the numbers. Chicago's signage inspired it, and its narrow digits let a countdown fill the width. Atkinson Hyperlegible, from the Braille Institute, sets everything else. Both ship with the app, so the dashboard works with no internet access.
