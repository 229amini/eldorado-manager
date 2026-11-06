# Changelog

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

Turn off **Keep alerting for highest priority** in ntfy on your phone. Siren repeats alerts by itself, and ntfy's loop can't stop from a button.

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
