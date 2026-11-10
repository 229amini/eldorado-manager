# Contributing

## Run it

You need Python 3.11 or newer. There's nothing to install.

```sh
python -m unittest discover -s tests -t .   # tests
python -m sellerdesk demo --scene hurry          # dashboard on a fake account
```

The Windows app needs the .NET SDK on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File desktop\build.ps1
```

To work on its dashboard page without an Eldorado key, open `desktop/SellerDesk/Web/index.html#dark` in a browser. It loads made-up data from `sample.js`.

## Rules for changes

- Keep the server on the standard library. Zero dependencies is a feature: sellers run this on small machines, and it has no supply chain to attack.
- Add a test with each behavior change. `tests/test_engine.py` has a fake clock and a fake API, so you can test hours of alarm behavior in a millisecond.
- A failing channel must never stop other channels or the poll loop.
- The server reads by default. Anything it changes on a seller's account needs a setting that starts off.
- The Windows app writes only when the seller acts. Nothing writes on a timer, and order actions ask twice.

## Add a channel

[docs/channels.md](docs/channels.md) has a full example. A channel pull request needs the class, a test, and a section in that page.

## Report an API change

If Eldorado changes a field and SellerDesk breaks, open an issue with the error from `docker compose logs sellerdesk`. Remove buyer names and order ids first.

## Security

See [SECURITY.md](SECURITY.md). Please don't open a public issue for a vulnerability.
