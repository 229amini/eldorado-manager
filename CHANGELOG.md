# Changelog

## 1.4.0

The code follows the new name. If you run the server, update your commands:

- Python package `eldorado_manager`: `python -m eldorado_manager run`, `check` and `demo`
- Docker Compose service is now `manager`: `docker compose logs manager`. The state volume is now `manager-data`; copy `state.json` over from the old volume to keep acknowledged orders.
- ntfy actions send `X-Manager-*` headers. The Windows app still reads the old ones from older servers.
- The web dashboard asks for its token once more

## 1.3.0

The project is now called Eldorado Manager, and the Windows app follows: `EldoradoManagerSetup.exe` installs `EldoradoManager.exe` in `%LOCALAPPDATA%\EldoradoManager`. Run it over 1.2.0 and it carries your settings, history and market data across, then removes the old install.

- Windows: an **Alarm on / off** switch in the dashboard and in Settings. Turn it off when another app already rings for your orders. The market estimate keeps running.
- Windows: closed offers take a new price and stock on the row, and **Reopen offer** sends both to list the offer again
- Windows: the market table shows **Sold at**, the middle price of the sales it saw, and **Most sales**, the offer that sold most with a link to it. These replace **Typical**, the middle of listed prices, which a few overpriced offers could pull far above what buyers pay.
- Server: a webhook channel posts each alert as JSON to a URL you choose

Price edits, stock edits and **Reopen offer** have run on a live account. Pause, deliver, extend and bulk changes still haven't.

## 1.2.0

The Windows app is new. It replaces the Python alarm and adds a shop dashboard.

- Offers: place on the site, price rank, edits on the row, a comparison with the top 5 sellers, and bulk changes
- Orders: open orders and the last 30 days, with Mark delivered and Extend
- Sales: sold and cleared totals, best sellers, items worth bringing back
- Insights: late delivery losses, buying hours, suggestions, the effect of price changes, and a market demand estimate from public listings
- The dashboard works with no server: leave the alarm topic empty
- The alarm shows a countdown, a floating island and a full-screen stage
- Dark and Light themes
- ntfy: the order link moved from a tap to an **Open** button, so tapping an alert no longer opens a browser
- ntfy: alerts carry the deadline for the Windows countdown

Turn off **Keep alerting for highest priority** in ntfy on your phone. The server repeats alerts by itself, and ntfy's loop can't stop from a button.

Price edits have run on a live account. Stock, pause, deliver, extend and bulk changes follow Eldorado's API reference but haven't run on a live account yet.

## 1.0.1

- Link the container image to this repository so GitHub Packages accepts it

## 1.0.0

First public release.

- Alarms that follow each order's delivery deadline, with four stages and a repeat budget per stage
- ntfy, Telegram, Discord and Pushover channels
- Live dashboard with a fake-account demo mode
- Opt-in dead-man switch
- One-file Windows installer
- Hardened Docker setup
