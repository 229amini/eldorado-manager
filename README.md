<h1 align="center">Eldorado Manager</h1>

<p align="center">
  Eldorado.gg has no webhooks and easy-to-miss notifications.<br>
  Eldorado Manager rings your phone and PC on every order, and shows you where your shop stands.
</p>

<p align="center">
  <a href="https://github.com/229amini/eldorado-manager/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/229amini/eldorado-manager/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/229amini/eldorado-manager/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/229amini/eldorado-manager"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue"></a>
  <img alt="Zero dependencies" src="https://img.shields.io/badge/dependencies-0-brightgreen">
</p>

<p align="center"><img src="docs/assets/siren.gif" alt="The Eldorado Manager dashboard turning from blue to amber to red as a delivery deadline gets close" width="820"></p>

## Pick what you need

| You want | Use | You need |
|---|---|---|
| Your shop, rank and sales in one window | [Windows app](#windows-app) | A Windows PC and an Eldorado Seller API key |
| Phone, watch and PC alarms | [Server](#set-up-the-server) | A machine that stays on, with Docker |
| Both | Both | Both |

Everything runs on your own machines. Eldorado Manager has no account to sign up for and no service of its own.

## Alarms

A buyer pays. Within 30 seconds your phone rings, your watch buzzes, a red window opens on your PC, and your second monitor counts down to the delivery deadline. Tap **Got it** on any device and the rest go silent.

- **The alarm knows your deadline.** Eldorado Manager reads each offer's guaranteed delivery time. A 20 minute order rings harder than a 7 day order, and it speeds up as time runs out.
- **Got it means "I saw this", and Eldorado Manager remembers.** Answer the alarm and it stays silent until the order enters its last 20%. If you forgot, it comes back.
- **Pick your channels.** ntfy, Telegram, Discord and Pushover work today. A new channel takes about 20 lines of Python.
- **The dashboard reads from across the room.** The whole page changes color with your most urgent order, and a beam sweeps the screen until someone answers.
- **The dead-man switch protects your rating.** Asleep with an unanswered alarm? Eldorado Manager can take you offline so no more orders land on you. It stays off until you enable it.
- **Eldorado Manager tells you when it breaks.** If it can't reach Eldorado for 10 minutes, or your credential expires, you get a full-volume alert.

The server runs on the Python standard library. No packages, no database, 128 MB of memory.

## Windows app

<p align="center"><img src="docs/assets/app-offers.png" alt="The Eldorado Manager dashboard: offers with their place on the site, price rank and stock, next to a comparison with the top sellers" width="820"></p>

1. Download [SirenSetup.exe](https://github.com/229amini/eldorado-manager/releases/latest) and double-click it. It needs no admin rights and no Python.
2. Windows may warn about an unknown app, because the installer has no paid certificate. Click **More info**, then **Run anyway**.
3. Create a key on [Dashboard > Seller API](https://www.eldorado.gg/dashboard/seller-api), and paste it under **Eldorado key** in Settings.

You then get:

- **Offers:** your place on the site, your price rank, and price and stock you edit on the row. A side panel compares you with the top 5 sellers.
- **Orders:** open orders and the last 30 days, with Mark delivered and Extend.
- **Sales:** what you sold and what has cleared, your best sellers, and items worth bringing back.
- **Insights:** money lost to late delivery, the hours buyers buy, and an estimate of what sells on the market that you don't stock.
- **The PC alarm:** a red window above your game, once you connect a server.

[The Windows app guide](docs/desktop.md) explains each tab, its limits and how to fix problems.

## Try the server in 30 seconds

You don't need an account or credentials for this.

```sh
git clone https://github.com/229amini/eldorado-manager.git
cd eldorado-manager
python -m siren demo --scene critical
```

Open <http://127.0.0.1:8787>. Other scenes: `quiet`, `calm`, `hurry`, `late`. Click **Got it** to stop the beam.

## Set up the server

You need a machine that stays on, such as a small VPS, and Docker.

1. Open [Dashboard > Seller API](https://www.eldorado.gg/dashboard/seller-api) on Eldorado and generate a credential.
2. Copy the settings file and add your credential plus one channel:
   ```sh
   cp .env.example .env
   ```
3. Test everything. This reads your account and changes nothing:
   ```sh
   docker compose run --rm siren python -m siren check
   ```
4. Start it:
   ```sh
   docker compose up -d
   ```

[Getting started](docs/getting-started.md) covers each step, including your phone and watch.

For the PC alarm, install the [Windows app](#windows-app) and enter your ntfy address and topic under **Alarm server**.

## How the alarm escalates

| Stage | When | Dashboard | Repeats every |
|---|---|---|---|
| Calm | More than 50% of the delivery window left | Blue | 60 s |
| Hurry | 50% to 20% left | Amber | 30 s |
| Critical | Last 20% | Red | 15 s |
| Late | Past the deadline | Dark red | 15 s |

A 7 day order would ring for days at those rates, so each stage gets a 30 minute budget. After that Eldorado Manager goes quiet until the next stage starts. You can change all of these numbers: see [Configuration](docs/configuration.md).

## Documentation

- [Getting started](docs/getting-started.md): from zero to a ringing phone
- [Channels](docs/channels.md): set up ntfy, Telegram, Discord and Pushover, or write your own
- [Configuration](docs/configuration.md): every setting
- [Deployment](docs/deployment.md): VPS, HTTPS, a private ntfy server, and building where Docker Hub is blocked
- [Windows app](docs/desktop.md): the installer, the dashboard and the PC alarm
- [How it works](docs/how-it-works.md): the code, for anyone changing it

## Is this safe for my account?

Both parts use Eldorado's official Seller API.

The server reads your orders and notifications. It never delivers, cancels or edits anything. The one action it can take is the dead-man switch, which you have to turn on, and that only flips you offline.

The Windows dashboard can change your account, and only when you tell it to: prices, stock, pausing, delivery time, Mark delivered and Extend. Nothing changes on a timer, and order actions ask twice.

Your credentials stay on your own machines: in `.env` for the server, encrypted with your Windows account for the app. Eldorado Manager sends data only to Eldorado and the channels you configure. See [SECURITY.md](SECURITY.md).

## Contributing

Bug reports and new channels are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) explains how to run the tests, which take less than a second.

## License

[MIT](LICENSE). Eldorado Manager is an independent project, and Eldorado.gg does not build, endorse or support it.
