"""The one HTTP helper every module shares. Stdlib only."""

from __future__ import annotations

import json
import urllib.error
import urllib.request

from . import __version__

USER_AGENT = f"sellerdesk/{__version__} (+https://github.com/229amini/sellerdesk)"


class HttpError(Exception):
    def __init__(self, status: int, url: str, body: str):
        super().__init__(f"HTTP {status} from {url.split('?')[0]}: {body[:200]}")
        self.status = status
        self.body = body


def request(method, url, *, headers=None, json_body=None, data=None, timeout=30) -> str:
    """Send a request and return the response text. Raises HttpError on a non-2xx status."""
    merged = {"User-Agent": USER_AGENT, "Accept": "application/json, text/csv, */*"}
    merged.update(headers or {})
    if json_body is not None:
        data = json.dumps(json_body).encode()
        merged.setdefault("Content-Type", "application/json")
    elif isinstance(data, str):
        data = data.encode()
    req = urllib.request.Request(url, data=data, headers=merged, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            return response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        raise HttpError(error.code, url, error.read().decode("utf-8", "replace")) from error
