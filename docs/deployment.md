# Deployment

## Docker

```sh
docker compose up -d                  # Siren alone
docker compose --profile ntfy up -d   # Siren and a private ntfy server
```

The containers run as a non-root user on a read-only filesystem, with all Linux capabilities dropped and memory limits set. Both publish their ports on `127.0.0.1` only.

To update:

```sh
git pull
docker compose up -d --build
```

Siren keeps its state in a Docker volume, so a rebuild doesn't repeat old alerts.

## Without Docker

Siren needs Python 3.11 or newer and nothing else.

```sh
set -a; . ./.env; set +a
STATE_FILE=./state.json python -m siren run
```

For a service that survives reboots, use this systemd unit:

```ini
[Unit]
Description=Siren
After=network-online.target

[Service]
User=siren
WorkingDirectory=/opt/eldorado-siren
EnvironmentFile=/opt/eldorado-siren/.env
Environment=STATE_FILE=/var/lib/siren/state.json
StateDirectory=siren
ExecStart=/usr/bin/python3 -m siren run
Restart=always

[Install]
WantedBy=multi-user.target
```

## Run your own ntfy server

Your order details then stay on your server.

1. In `.env`, set the address your phone will use, and the address Siren uses inside Docker:

   ```sh
   NTFY_PUBLIC_URL=https://ntfy.example.com
   NTFY_URL=http://ntfy:2586
   NTFY_TOPIC=eldorado
   ```

2. Start ntfy and hand its data volume to the container's user, one time:

   ```sh
   docker compose --profile ntfy create ntfy
   docker run --rm --user 0 --entrypoint chown \
     -v eldorado-siren_ntfy-data:/data binwiederhier/ntfy:v2.37.0 -R 10001:10001 /data
   docker compose --profile ntfy up -d ntfy
   ```

3. Create a user for your phone, and a limited one for Siren:

   ```sh
   docker compose exec ntfy ntfy user add phone
   docker compose exec ntfy ntfy access phone 'eldorado*' rw
   docker compose exec ntfy ntfy user add siren
   docker compose exec ntfy ntfy access siren 'eldorado*' rw
   docker compose exec ntfy ntfy token add siren
   ```

4. Put the token in `.env` as `NTFY_TOKEN`, then restart:

   ```sh
   docker compose --profile ntfy up -d
   ```

Keep the Siren user non-admin. Siren puts its token inside each alert so the "Got it" button can publish, which means anyone who can read the topic can read that token.

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

The only state worth keeping is `.env`. Siren rebuilds the rest on its next check.
