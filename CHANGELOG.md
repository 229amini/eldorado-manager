# Changelog

## Unreleased

- Server: a market read that fails to reach Eldorado (a DNS hiccup or a dropped connection) goes again after 5 and then 20 seconds. Before, one such page threw away the whole half-hourly sweep, and on a flaky link no sweep finished for hours.

## 1.6.3

- SellerDesk updates itself. When a newer version is out, a line at the top of the dashboard says so, with **What's new** and **Update now**. One click downloads it, checks it against GitHub's SHA-256 checksum, installs it and opens the dashboard again, with your settings and data kept. Versions before 1.6.3 have to install this one by hand.
- The release page now lists each version's changes from this file.

## 1.6.2

- Market prices now account for bundle sizes. Sellers list the same item as "100x", "500" or a single one, and Eldorado gives only the price of one unit, so a typical price used to mix bundles of every size. SellerDesk reads the count from each offer's title, works every price out per item and shows it for the size of your own offer. If it reads your offer wrong, type the right count in the side panel.
- Offers: drag your mark on the sales line in the side panel, or use the arrow keys, to try a price. It shows where you would place and how far you are from the typical sale, and sends nothing until you press **Save price**.
- Server: sales recorded before this version take the bundle size of their offer if it is still listed. The rest drop out of the numbers, which may show fewer sales for up to a week. Update the server before the desktop app; an older server sends no bundle sizes and the app then shows no sales.

## 1.6.1

- Finance: each order under **To assign** has a button per person that gives them every unit left and saves in one click. The split panel gives each person **All**, **Rest** and **Half** next to **−** and **+**.
- Finance: a **Per person** view shows one column per person with their orders, so a wrong assignment is easy to spot and fix with one click.
- Finance: order rows show the item's picture.
- Offers: price and stock boxes fit 5 digits. **Sold at** is now **Others sold at**, and the price line in the side panel names each of its marks.
- **Open offer** from Insights or Home now scrolls to the offer's row and selects it, even when a search was typed earlier.

## 1.6.0

- The app is now called **SellerDesk**. Installing 1.6.0 moves your settings, seller key, history and market data over from Eldorado Manager and removes the old app, so there is nothing to set up again.
- A new look: calmer greys, thin borders, a new font and a new logo, in both Dark and Light.
- Server: the Python package and the compose service are both called `sellerdesk` now. If you have your own `docker-compose.override.yml`, rename the service there too.

## 1.5.0

- Windows: a **Home** tab opens first, with today's sales, what needs you now, price checks and items worth adding
- Windows: an edit Eldorado refuses stays on its row in red with the value you typed, a reason, **Send again** and **Discard my change**. Before, the row went back to the old price and the error showed only in the side panel, so a sale could go through at a price you thought you'd changed. A rate-limit refusal goes again every 90 seconds, and the app reads your offers after each save to check the new price is there.
- Windows: **Reopen offer** sends the stock before the price. Eldorado refuses a price on an offer with no stock, and some offers need a minimum stock, which the red line now names.
- Windows: a **Sold at** column on the Offers tab, and a price range in the side panel built from the sales other sellers made this week, with **Set price to** the middle of them
- Windows: a **Customers** tab with repeat buyers, a weekday and hour grid, best buyers, buyers worth watching, and items bought together
- Windows: a **Finance** tab. Assign each sale's units to the people you sell for, record what you pay them, and export each person's work list to Excel. Shares count the money after Eldorado's fee.
- Windows: the dashboard sizes itself to your screen, and Ctrl with + or - changes it
- Server: a market hub reads the public listings once for every PC and serves them at `GET /api/market`. Point the Windows app at it under **Settings > Eldorado key > Shared server**, and your PCs stop sharing one rate limit.
- Server: a finance ledger at `/api/ledger`, with fees read from Eldorado's sales export once an order clears. New settings: `MARKET_ENABLED`, `MARKET_PASS_SECONDS`, `MARKET_SWEEP_MINUTES`, `MARKET_FILE`, `LEDGER_FILE`.
- Server: the market and finance API answer only when `DASHBOARD_TOKEN` is set. The local web page works without one, as before.
- Server: when two PCs edit the same order or person, the second save gets "Changed on another PC" and the app shows what the first one saved, so neither edit vanishes
- Server: a write without `Content-Length` gets 411 instead of reading as empty, and saving an order needs its `shares` list, so a lost request body can't remove an order
- Server: the ledger syncs each save to disk, keeps a damaged file aside instead of writing over it, and refuses amounts above 1,000,000,000 and duplicate names

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
