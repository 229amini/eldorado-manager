# Windows app

One installer gives you two things:

- **The alarm.** A red window opens above your game and an alarm sound loops until you answer. It listens to your [Eldorado Manager server](getting-started.md) through ntfy.
- **The dashboard.** Your whole shop in one window: offers, rank against other sellers, orders, sales and insights. It talks to Eldorado with your own Seller API key.

Use both, or either one alone. The dashboard needs no server.

| You want | You need |
|---|---|
| Dashboard only | This app and an Eldorado Seller API key |
| PC alarm | This app, plus a running Eldorado Manager server with ntfy as a channel |
| Both | All of the above |

The alarm won't hear Telegram, Discord or Pushover alerts. It listens to ntfy only.

## Install

You need Windows 10 or 11. You don't need admin rights or Python.

1. Download `EldoradoManagerSetup.exe` from the [latest release](https://github.com/229amini/eldorado-manager/releases/latest).
2. Double-click it.
3. Windows may warn that it doesn't recognize the app, because the installer has no paid code-signing certificate. Click **More info**, then **Run anyway**. The source is in [`desktop/Siren`](../desktop/Siren), and you can [build it yourself](#build-it-yourself).
4. Fill in Settings. It opens by itself the first time.

To update, run the new `EldoradoManagerSetup.exe` over the old one. Your settings stay. Version 1.2.0 and older installed as Siren; the new installer moves your settings and data over and removes the old app.

## First-time settings

Fill in one tab or both, then click **Save and start**. Eldorado Manager tests each connection before it saves.

**Alarm server**, for the PC alarm. Enter the same ntfy address, topic and token as your server's `.env`. For the dashboard alone, leave the topic empty.

**Eldorado key**, for the dashboard:

1. Open [Dashboard > Seller API](https://www.eldorado.gg/dashboard/seller-api) on Eldorado.
2. Name the credential and pick an expiry. The longest option is 365 days.
3. Click **Generate credentials**, then copy the Client ID and Client secret into Eldorado Manager.

Eldorado shows the secret once. Eldorado Manager encrypts it with your Windows account, so another user or a copied config file can't read it. This key can change your prices, so treat it like a password.

After that, the alarm starts each time you sign in. Open the dashboard from the Start menu: **Eldorado Manager**. Repeat these steps on each PC you use.

## Answer an alarm

Click **Got it**, or close the window. Both tell every device you saw it. Tapping **Got it** on your phone closes the window on your PC.

## Silence the alarm

If another app already rings for your orders, click **Alarm on** at the top of the dashboard. It turns to **Alarm off** and new orders stop ringing on this PC. Click it again to bring the sound back. You can set the same thing under **Settings > Alarm server**. The market estimate keeps running either way.

## The dashboard

The strip under the tabs shows what needs you right now: orders to deliver, disputes, offers that slipped, offers out of stock. When nothing does, it says so.

### Offers

![The Offers tab: a list of offers with place, price rank and stock, and a side panel comparing one offer with the top sellers](assets/app-offers.png)

Each offer takes one row:

- **Place** is where buyers find the offer in the site's default order. The arrow shows how far it moved since you opened the dashboard.
- **Price rank** counts sellers by price alone. The two often disagree: Eldorado doesn't rank by price.
- **Sold 30 d** comes from your order list.

Type a new price or stock on the row. Eldorado Manager sends it 6 seconds after your last change, or right away when you press Enter. A price change over 50% asks you to confirm.

Click a row to open the side panel. It compares your price, delivery time and ratings with the top 5 sellers and names your biggest gap.

**Change many** changes one game's prices by a percentage, or its delivery time. You review the list before it sends.

Eldorado closes an offer when its stock runs out. Under **Closed**, type a price and a stock above 0 on the row, then click **Reopen offer** in the side panel. The new stock lists the offer again. The next check shows where it landed.

### Orders

Open orders and the last 30 days, with buyer, amount, deadline, review and dispute. **Mark delivered** and **Extend** act on a real order, so both ask a second time.

### Sales

![The Sales tab: totals, a daily chart, best sellers and items worth bringing back](assets/app-sales.png)

Pick 7, 30 or 90 days:

- **Sold** counts orders by the day the buyer paid.
- **Net income cleared** is money Eldorado has settled. Orders take about 5 days to clear, so the card also shows what's still on its way.
- **Best sellers** ranks your items and shows each one's state today.
- **Worth bringing back** lists items that sold in the last 90 days but are gone, paused or nearly out.

### Insights

![The Insights tab: suggestions, a chart of sales by hour, and a table of what sells on the market](assets/app-insights.png)

- **Top cards** show money lost to late delivery, your real delivery time against your promise, repeat buyers and reviews.
- **Worth doing** suggests moves, such as a price with room to rise or stock about to run out. Eldorado Manager changes nothing by itself.
- **When buyers buy** charts sales and late cancellations by hour, in your local time.
- **Selling on the market** estimates demand. See the next section.
- **What your price changes did** compares place and sales before and after each change. It needs a few days of data.

### How the market estimate works

Eldorado publishes no sales numbers per item. So every 30 minutes Eldorado Manager reads the first 1,500 public offers of each listing you sell in, and counts each time another seller's stock falls.

Each row shows the cheapest price listed right now, and two numbers from the falls themselves:

- **Sold at** is the middle price of the sales it saw, one vote per sale. Price near this number when you list the item. The cheapest offer often sits there with no buyers.
- **Most sales** names the offer that sold most and its price. Click it to open that offer on Eldorado and compare its delivery time and ratings with yours.

Both start empty and fill in after the second check.

Read it as a hint. Stock also falls when a seller edits it by hand, and Eldorado Manager sees only the top of a large listing. The list labels itself **Early** until it has a few days of data. It keeps collecting while the alarm runs, even with the dashboard closed, and it needs no key.

## Change settings

Click **Settings** in the dashboard, or open **Eldorado Manager settings** from the Start menu. The moon and sun button switches between Dark and Light.

To use your own alarm sound, add a path to a `.wav` file as `"sound"` in `%LOCALAPPDATA%\EldoradoManager\config.json`, then restart the app.

## Known limits

- Rank, lowest price and **Match lowest price** work for offers priced in USD. Public listings show USD only, so the other offers show **USD only** in those columns.
- Sales totals add up every order as USD.
- Eldorado has no call for all your currency offers, so the dashboard asks per game your item offers are in. A seller with currency offers only won't see them yet.
- Eldorado's API has no chat, balance or offer photo calls, so the app doesn't either.
- The maintainer has tested price edits, stock edits and **Reopen offer** on a live account. Pause, deliver, extend and **Change many** follow Eldorado's API reference but haven't run on a live account yet. Try them on a cheap offer first.

## Uninstall

Open **Settings > Apps > Installed apps**, find **Eldorado Manager**, and click **Uninstall**. That removes the app, its settings and its log.

## Troubleshooting

Eldorado Manager writes a log to `%LOCALAPPDATA%\EldoradoManager\manager.log`.

| What you see | What to do |
|---|---|
| No `connected` line in the log | Your ntfy address or token is wrong |
| `connected`, but no alarm | The app's topic differs from `NTFY_TOPIC` on your server |
| Dashboard says it needs WebView2 | Install the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). Windows 11 has it already |
| `401` from Eldorado | The key has a typo or has expired. Generate a new one and enter it under **Eldorado key** |
| `429` from Eldorado | You've hit Eldorado's rate limit. Eldorado Manager slows down, so wait a few minutes |
| A price is refused | Eldorado takes 2 decimals, or up to 5 below 0.01 |
| The taskbar shows an old icon after an update | Sign out of Windows and back in |

## Build it yourself

You need the [.NET SDK](https://dotnet.microsoft.com/download) and the .NET Framework 4.8 Developer Pack.

```powershell
powershell -ExecutionPolicy Bypass -File desktop\build.ps1
```

The result is `desktop\dist\EldoradoManagerSetup.exe`. On a tagged release, GitHub Actions runs this same script and attaches the exe.
