# Security

## Report a vulnerability

Use [GitHub's private reporting](https://github.com/229amini/eldorado-siren/security/advisories/new). Please don't open a public issue.

## What Siren holds

- Your Eldorado Client ID and secret, in `.env`. Anyone who has them can manage your offers and orders, so run `chmod 600 .env` and keep the file out of git. Siren's `.gitignore` already skips it.
- Your channel tokens, in the same file.
- A state file with order ids and notification ids. It holds no credentials.

If a secret leaks, revoke it on the Eldorado Seller API page at once and generate a new one.

## What leaves your machine

Siren talks to Eldorado and to the channels you configure, and nothing else. Alerts contain the offer title, price and buyer name. On the public ntfy.sh server, anyone who knows your topic can read them, so pick a long random name or [run your own server](docs/deployment.md#run-your-own-ntfy-server).

Siren puts your ntfy token inside each alert so the "Got it" button works. Give Siren a non-admin ntfy user with access to its own topics.

## Dashboard

The dashboard listens on localhost by default. It has no token until you set `DASHBOARD_TOKEN`, so set one before you expose the port. The token protects both reading the page and its actions.
