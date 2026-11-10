# Deployment

## Docker

```sh
docker compose up -d                  # Eldorado Manager alone
docker compose --profile ntfy up -d   # Eldorado Manager and a private ntfy server
```

The containers run as a non-root user on a read-only filesystem, with all Linux capabilities dropped and memory limits set. Both publish their ports on `127.0.0.1` only.

To update:

```sh
git pull
docker compose up -d --build
```

Eldorado Manager keeps its state in a Docker volume, so a rebuild doesn't repeat old alerts.

## Without Docker

Eldorado Manager needs Python 3.11 or newer and nothing else.

```sh
set -a; . ./.env; set +a
STATE_FILE=./state.json python -m eldorado_manager run
```

For a service that survives reboots, use this systemd unit:

```ini
[Unit]
Description=Eldorado Manager
After=network-online.target

[Service]
User=eldorado-manager
WorkingDirectory=/opt/eldorado-manager
EnvironmentFile=/opt/eldorado-manager/.env
Environment=STATE_FILE=/var/lib/eldorado-manager/state.json
StateDirectory=eldorado-manager
ExecStart=/usr/bin/python3 -m eldorado_manager run
Restart=always

[Install]
WantedBy=multi-user.target
```

## Run your own ntfy server

Your order details then stay on your server.

1. In `.env`, set the address your phone will use, and the address Eldorado Manager uses inside Docker:

   ```sh
   NTFY_PUBLIC_URL=https://ntfy.example.com
   NTFY_URL=http://ntfy:2586
   NTFY_TOPIC=eldorado
   ```

2. Start ntfy and hand its data volume to the container's user, one time:

   ```sh
   docker compose --profile ntfy create ntfy
   docker run --rm --user 0 --entrypoint chown \
     -v eldorado-manager_ntfy-data:/data binwiederhier/ntfy:v2.37.0 -R 10001:10001 /data
   docker compose --profile ntfy up -d ntfy
   ```

3. Create a user for your phone, and a limited one for Eldorado Manager:

   ```sh
   docker compose exec ntfy ntfy user add phone
   docker compose exec ntfy ntfy access phone 'eldorado*' rw
   docker compose exec ntfy ntfy user add manager
   docker compose exec ntfy ntfy access manager 'eldorado*' rw
   docker compose exec ntfy ntfy token add manager
   ```

4. Put the token in `.env` as `NTFY_TOKEN`, then restart:

   ```sh
   docker compose --profile ntfy up -d
   ```

Keep the Eldorado Manager user non-admin. Eldorado Manager puts its token inside each alert so the "Got it" button can publish, which means anyone who can read the topic can read that token.

ntfy needs HTTPS in front of it, or your token travels in clear text. See the next section.

## HTTPS

Examples for both common web servers:

- [`deploy/Caddyfile.example`](../deploy/Caddyfile.example): the short one, which fetches certificates for you.
- [`deploy/nginx.conf.example`](../deploy/nginx.conf.example): for a server already running nginx.

If nginx already serves a wildcard name such as `*.example.com`, issue your certificate with `certbot certonly --webroot`. `certbot --nginx` can install the new certificate into the wildcard block and break the other site.

## Reach the dashboard from another device

Set `DASHBOARD_TOKEN` to a long random string first. Then pick one:

- An SSH tunnel, with nothing public: `ssh -L 8787:127.0.0.1:8787 you@your-server`, then open <http://127.0.0.1:8787>.
- A private network such as Tailscale or WireGuard.
- HTTPS through Caddy or nginx.

The dashboard asks for the token once and keeps it in that browser.

## Share the API with the Windows app

The Windows app reads the market and the Finance ledger from your server when you give it the server's address and `DASHBOARD_TOKEN`. It only needs the `/api/` paths, so publish those alone over HTTPS. With nginx, add this to the `server` block that already serves your HTTPS name:

```nginx
location ^~ /manager/api/ {
    proxy_pass http://127.0.0.1:8787/api/;
    proxy_http_version 1.1;
    client_max_body_size 64k;
    proxy_set_header Host $http_host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}
```

Run `nginx -t`, reload nginx, then enter `https://your-name/manager` as the server address in the Windows app. Set `DASHBOARD_TOKEN` before you publish: without it the server answers 403 to every market and ledger request. The token sits in the app's settings, encrypted with your Windows account.

The market hub calls Eldorado about once every 1.5 seconds while it reads, and holds off for as long as Eldorado asks when it hits the rate limit. Turn it off with `MARKET_ENABLED=false` if you only want alarms.

## Where Docker Hub is blocked

Docker Hub refuses connections from some countries, including Iran. Point the build at a mirror:

```sh
PYTHON_IMAGE=docker.arvancloud.ir/library/python:3.12-alpine
NTFY_IMAGE=docker.arvancloud.ir/binwiederhier/ntfy:v2.37.0
```

A mirror can serve you whatever it likes, so compare digests before you trust it. Run this on the server:

```sh
docker image inspect "$NTFY_IMAGE" --format '{{.RepoDigests}}'
```

Then compare the result with the digest of that tag on Docker Hub, from any connection that can reach it.

## Backups

The only state worth keeping is `.env`. Eldorado Manager rebuilds the rest on its next check.
