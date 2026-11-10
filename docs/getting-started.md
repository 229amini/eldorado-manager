# Getting started

This guide takes you from nothing to a phone that rings on a new order. Plan on 15 minutes.

If you only want the shop dashboard on Windows, you can skip all of this. Go to [Windows app](desktop.md).

## What you need

- A verified Eldorado seller account.
- A machine that stays on. A small Linux VPS works well. Your own PC works too, but SellerDesk stops when the PC sleeps.
- Docker with the compose plugin. To run without Docker, see [Deployment](deployment.md).

## 1. Get your Eldorado credential

1. Open [Dashboard > Seller API](https://www.eldorado.gg/dashboard/seller-api).
2. Name the credential `SellerDesk` and pick an expiry. The longest option is 365 days.
3. Click **Generate credentials** and copy the Client ID and Client secret.

Eldorado shows the secret once. When the credential expires, SellerDesk sends you a full-volume alert and you generate a new one.

## 2. Download SellerDesk and add your settings

```sh
git clone https://github.com/229amini/sellerdesk.git
cd sellerdesk
cp .env.example .env
chmod 600 .env
```

Put the credential into `.env`:

```sh
ELDORADO_CLIENT_ID=your-client-id
ELDORADO_CLIENT_SECRET=your-client-secret
```

## 3. Pick a channel

The fastest choice is the free public ntfy server:

1. Install the [ntfy app](https://ntfy.sh/docs/subscribe/phone/) on your phone.
2. Invent a long random topic name, because anyone who knows the name can read it. For example: `eldorado-7f3kq9x2mz`.
3. Subscribe to that topic in the app.
4. Add it to `.env`:

   ```sh
   NTFY_URL=https://ntfy.sh
   NTFY_TOPIC=eldorado-7f3kq9x2mz
   ```

Your order titles and buyer names pass through that public server. For privacy, [host your own](deployment.md#run-your-own-ntfy-server), or use another channel from [Channels](channels.md).

## 4. Test it

```sh
docker compose run --rm sellerdesk python -m sellerdesk check
```

You should see your order counts, and your phone should ring with a test alert. This command changes nothing on your account.

If a line says `FAILED`, it names the reason. A `401` from Eldorado means the Client ID or secret has a typo.

## 5. Start it

```sh
docker compose up -d
docker compose logs -f sellerdesk
```

Open <http://127.0.0.1:8787> on that machine to see the dashboard. To reach it from elsewhere, read [Deployment](deployment.md#reach-the-dashboard-from-another-device).

## 6. Make your phone loud

Android keeps killing background apps unless you tell it not to. On your phone:

1. Open the app's system settings and allow notifications at the highest level.
2. Turn on **Autostart**, if your phone has that option. Xiaomi, Oppo and Vivo phones do.
3. Set the battery setting to **Unrestricted** or **No restrictions**.
4. In ntfy settings, turn off **Keep alerting for highest priority**. SellerDesk repeats the alert until you answer, so you don't need it, and ntfy's loop keeps ringing after you tap **Got it**.

Tap **Got it** to stop the alarm on every device. Tapping the notification itself silences that one ring, and SellerDesk rings again on its next repeat. **Open** takes you to the order on Eldorado.

## 7. Get it on your watch

Your watch shows what your phone shows, so you only need to allow one app:

| Watch | Where to allow it |
|---|---|
| Xiaomi or Redmi | Mi Fitness > Notifications |
| Samsung Galaxy | Galaxy Wearable > Watch settings > Notifications |
| Wear OS | The watch companion app > Notifications |
| Apple Watch | Watch app on iPhone > Notifications |

## 8. Add the PC alarm

Download `SellerDeskSetup.exe` from the [latest release](https://github.com/229amini/sellerdesk/releases/latest) and run it. Under **Alarm server**, enter the same ntfy address and topic you put in `.env`. Add your Eldorado key under **Eldorado key** and you get the shop dashboard too. See [Windows app](desktop.md).

## You're done

SellerDesk sends "SellerDesk is on watch" each time it starts. Place a cheap test order from a second account, or wait for a real one.
