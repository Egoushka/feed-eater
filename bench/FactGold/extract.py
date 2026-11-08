#!/usr/bin/env python3
"""Run the fact extractor over the gold set through the Bifrost gateway.

  extract.py --model openrouter/deepseek/deepseek-v4.1-flash --tag deepseek-p1 [--prompt prompt.md] [--only ID,ID]

Writes results/<tag>.json: {"model", "prompt", "items": {id: {"facts": [...], "usage": {...}, "error": ...}}, "usage": {...}}.
The key is read from ~/.config/opencode/secrets/litellm-opencode and is never printed or stored.
Output is the camelCase fact shape Switchknife.Facts takes (wire format v1): kind in `predicate`, claim in `text`.
"""

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE = os.environ.get("BIFROST_URL", "http://100.64.0.4:4001/v1")
KEYFILE = os.path.expanduser("~/.config/opencode/secrets/litellm-opencode")
# USD per million tokens, starting values for a cost estimate; the real figure comes from `lab spend`.
PRICE = {
    "openrouter/deepseek/deepseek-v4.1-flash": (0.15, 0.60),
    "gemini/gemini-3.5-flash-lite": (0.10, 0.40),
}
MAX_TEXT = 6000


def call(model, system, user, key):
    body = {
        "model": model,
        "temperature": 0,
        "max_tokens": 6000,
        "response_format": {"type": "json_object"},
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ],
    }
    req = urllib.request.Request(
        BASE + "/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)


def parse(content):
    if not content:
        raise ValueError("empty content")
    content = content.strip()
    m = re.search(r"\{.*\}", content, re.DOTALL)
    data = json.loads(m.group(0) if m else content)
    facts = data.get("facts", [])
    return [f for f in facts if isinstance(f, dict)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--prompt", default=str(HERE / "prompt.md"))
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    key = Path(KEYFILE).read_text().strip()
    gold = json.loads((HERE / "gold.json").read_text())
    system = Path(a.prompt).read_text()
    only = set(filter(None, a.only.split(",")))
    out = {"model": a.model, "prompt": os.path.basename(a.prompt), "items": {}}
    tin = tout = 0
    for it in gold["items"]:
        if only and it["id"] not in only:
            continue
        user = f"Item date: {it['source_date']}\nSource: {it['source_url']}\nTitle: {it['title']}\n\n<item_text>\n{it['text'][:MAX_TEXT]}\n</item_text>"
        rec = {"facts": [], "usage": {}, "error": None}
        for attempt in range(2):
            try:
                resp = call(a.model, system, user, key)
                rec["usage"] = resp.get("usage", {})
                rec["facts"] = parse(resp["choices"][0]["message"]["content"])
                rec["error"] = None
                break
            except (
                urllib.error.URLError,
                ValueError,
                KeyError,
                AttributeError,
                json.JSONDecodeError,
                TimeoutError,
            ) as e:
                rec["error"] = f"{type(e).__name__}: {str(e)[:120]}"
                time.sleep(2)
        tin += rec["usage"].get("prompt_tokens", 0)
        tout += rec["usage"].get("completion_tokens", 0)
        out["items"][it["id"]] = rec
        print(
            f"{it['id']:24} facts={len(rec['facts'])} {rec['error'] or ''}",
            file=sys.stderr,
        )
    pin, pout = PRICE.get(a.model, (0.0, 0.0))
    out["usage"] = {
        "prompt_tokens": tin,
        "completion_tokens": tout,
        "est_usd": round(tin / 1e6 * pin + tout / 1e6 * pout, 4),
    }
    (HERE / "results").mkdir(exist_ok=True)
    path = HERE / "results" / f"{a.tag}.json"
    path.write_text(json.dumps(out, ensure_ascii=False, indent=1))
    print(
        f"wrote {path} tokens in={tin} out={tout} est_usd={out['usage']['est_usd']}",
        file=sys.stderr,
    )


if __name__ == "__main__":
    main()
