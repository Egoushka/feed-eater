#!/usr/bin/env python3
"""B1 of the prior-art benchmark: Hindsight as the lookup.

  b1_hindsight.py load   FILE...            retain the seed text into the scratch bank eval-prior-art (one document per file) and wait
  b1_hindsight.py recall --gold F --out F   top 10 recalled facts per gold question, as {"<id>": ["text", ...]} for `recall --b1`
  b1_hindsight.py cleanup                   delete the scratch bank (only eval-* banks)

HINDSIGHT_URL defaults to the tailnet; the key comes from HINDSIGHT_API_KEY or the Keychain item (service mcp, account HINDSIGHT_API_KEY).
"""
import json, os, subprocess, sys, time
from urllib import error, request

URL = os.environ.get("HINDSIGHT_URL", "http://100.64.0.2:8888").rstrip("/")
BANK = "eval-prior-art"
DONE = {"completed", "failed", "cancelled", "error"}


def key():
    k = os.environ.get("HINDSIGHT_API_KEY", "")
    if not k:
        r = subprocess.run(["security", "find-generic-password", "-s", "mcp", "-a", "HINDSIGHT_API_KEY", "-w"], capture_output=True, text=True)
        k = r.stdout.strip() if r.returncode == 0 else ""
    return k


def call(method, path, body=None, timeout=300):
    headers = {"Content-Type": "application/json"}
    if key():
        headers["Authorization"] = f"Bearer {key()}"
    req = request.Request(f"{URL}/v1/default/banks/{BANK}{path}", method=method, headers=headers, data=None if body is None else json.dumps(body).encode())
    with request.urlopen(req, timeout=timeout) as r:
        text = r.read().decode()
        return json.loads(text) if text else {}


def wait(op, timeout=1800):
    end = time.time() + timeout
    while time.time() < end:
        status = call("GET", f"/operations/{op}")["status"]
        if status in DONE:
            return status
        time.sleep(3)
    return "timeout"


def option(name):
    return sys.argv[sys.argv.index(name) + 1]


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "load":
        call("PUT", "", {"name": BANK, "mission": "Scratch bank for the prior-art benchmark (feed-eater bench/PriorArt.Bench): audit text about tools. Nothing here is about Yehor."})
        for path in sys.argv[2:]:
            name = os.path.basename(path)
            day = name[:10] if name[:4].isdigit() else "2026-10-29"
            item = {"content": open(path, encoding="utf-8").read(), "timestamp": f"{day}T12:00:00Z", "document_id": f"eval-prior-art-{name}",
                    "context": "An audit of developer tools and AI tooling: which tools exist, what they do, and a verdict."}
            status = wait(call("POST", "/memories", {"items": [item], "async": True})["operation_id"])
            print(f"{name}: {status}", flush=True)
    elif cmd == "recall":
        out = {}
        for line in open(option("--gold"), encoding="utf-8"):
            d = json.loads(line)
            r = call("POST", "/memories/recall", {"query": d["question"], "budget": "low", "max_tokens": 4096})
            out[str(d["id"])] = [x.get("text", "") for x in r.get("results", [])][:10]
        json.dump(out, open(option("--out"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"recalled {len(out)} questions, {sum(len(v) for v in out.values())} facts")
    elif cmd == "cleanup":
        try:
            call("DELETE", "")
            print(f"deleted scratch bank {BANK}")
        except error.HTTPError as e:
            if e.code != 404:
                raise
    else:
        sys.exit(__doc__)


main()
