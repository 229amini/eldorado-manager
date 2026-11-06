# Security

## Report a vulnerability

Use [GitHub's private reporting](https://github.com/229amini/eldorado-manager/security/advisories/new). Please don't open a public issue.

## What Eldorado Manager holds

- Your Eldorado Client ID and secret, in `.env`. Anyone who has them can manage your offers and orders, so run `chmod 600 .env` and keep the file out of git. Eldorado Manager's `.gitignore` already skips it.
- Your channel tokens, in the same file.
- A state file with order ids and notification ids. It holds no credentials.
- In the Windows app, your Eldorado Client ID and secret in `%LOCALAPPDATA%\Siren\config.json`. Eldorado Manager encrypts the secret with your Windows account (DPAPI), so another Windows user or a copy of the file can't read it. Your ntfy token sits in the same file unencrypted.
- In the same folder, `history.json` and `market.json`: your offers' daily price and place, and counts from public listings.

If a secret leaks, revoke it on the Eldorado Seller API page at once and generate a new one.

## What leaves your machine

Eldorado Manager talks to Eldorado and to the channels you configure, and nothing else. The Windows app loads offer images from Eldorado's image server. Alerts contain the offer title, price and buyer name. On the public ntfy.sh server, anyone who knows your topic can read them, so pick a long random name or [run your own server](docs/deployment.md#run-your-own-ntfy-server).

Eldorado Manager puts your ntfy token inside each alert so the "Got it" button works. Give Eldorado Manager a non-admin ntfy user with access to its own topics.

## What the Windows dashboard can change

With your key, the dashboard can change prices, stock, delivery times and paused offers, and mark orders delivered or extend them. It does these when you act, never on a timer. Order actions and price changes over 50% ask you to confirm.

The dashboard page shows text from other sellers' listings, so Eldorado Manager runs it with dev tools, host objects and the context menu turned off.

## Server dashboard

The dashboard listens on localhost by default. It has no token until you set `DASHBOARD_TOKEN`, so set one before you expose the port. The token protects both reading the page and its actions.
