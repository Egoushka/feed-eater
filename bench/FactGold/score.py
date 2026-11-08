#!/usr/bin/env python3
"""Score extracted facts against the fact gold set.

Usage:
  score.py --check-gold                      verify the gold file is well-formed and every anchor is in its source text
  score.py --self-test                       the scorer gives full marks to the gold's own claims and zero to nothing
  score.py RESULT.json [--min-salience 0.0]  print recall and precision, write nothing

Match rule (frozen before any model output was seen): an expected fact matches an
emitted fact of the same item when every group in its `match` list has at least one
alternative that is a substring of the normalised subject + text + value + object of
the emitted fact. Normalised = lower-case, `$` and `,` removed, spaces collapsed.
An emitted fact satisfies at most one expected fact. An item counts as recalled when at
least ceil(n/2) of its n expected facts match.
"""

import argparse
import json
import math
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
KINDS = {"opportunity", "price_change", "new_model", "code_idea", "idea"}


def norm(s):
    s = str(s or "").lower().replace("$", "").replace(",", "")
    s = s.replace("‑", "-").replace("–", "-").replace("’", "'")
    return re.sub(r"\s+", " ", s)


def blob(fact):
    subj = fact.get("subject") or {}
    return norm(
        " ".join(
            str(x)
            for x in (
                subj.get("name"),
                fact.get("text"),
                fact.get("value"),
                fact.get("object"),
            )
            if x
        )
    )


def hits(expected, emitted_blob):
    return all(
        any(norm(alt) in emitted_blob for alt in group) for group in expected["match"]
    )


def check_gold(gold):
    problems = []
    items = gold["items"]
    pos = [i for i in items if not i["negative"]]
    if len(pos) < 30:
        problems.append(f"only {len(pos)} positive items, need 30")
    for it in items:
        text = norm(it["text"])
        if not it["source_url"].startswith("http") or not text:
            problems.append(f"{it['id']}: missing url or text")
        if it["negative"] and it["facts"]:
            problems.append(f"{it['id']}: negative item has facts")
        if not it["negative"] and not it["facts"]:
            problems.append(f"{it['id']}: positive item has no facts")
        for n, f in enumerate(it["facts"]):
            if f["kind"] not in KINDS:
                problems.append(f"{it['id']}#{n}: unknown kind {f['kind']}")
            for g in f["match"]:
                if not any(norm(a) in text for a in g):
                    problems.append(
                        f"{it['id']}#{n}: no alternative of {g} appears in the snapshot"
                    )
            for key in ("deadline", "date"):
                if f.get(key) and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", f[key]):
                    problems.append(f"{it['id']}#{n}: bad {key}")
    return problems


def score(gold, result, min_salience):
    items_res = result["items"]
    rows = []
    tot_exp = tot_hit = emitted_all = emitted_matched = 0
    kind_ok = kind_n = dl_ok = dl_n = 0
    neg_dirty = 0
    for it in gold["items"]:
        res = items_res.get(it["id"], {})
        facts = [
            f for f in res.get("facts", []) if (f.get("salience") or 0) >= min_salience
        ]
        emitted_all += len(facts)
        used = set()
        matched = []
        for n, exp in enumerate(it["facts"]):
            for j, f in enumerate(facts):
                if j in used or not hits(exp, blob(f)):
                    continue
                used.add(j)
                matched.append(n)
                kind_n += 1
                kind_ok += f.get("predicate") in [exp["kind"], *exp.get("kind_alt", [])]
                if exp.get("deadline"):
                    dl_n += 1
                    dl_ok += str(f.get("validTo") or "").startswith(exp["deadline"])
                break
        emitted_matched += len(used)
        if it["negative"]:
            neg_dirty += bool(facts)
        else:
            tot_exp += len(it["facts"])
            tot_hit += len(matched)
        need = math.ceil(len(it["facts"]) / 2)
        rows.append(
            {
                "id": it["id"],
                "negative": it["negative"],
                "expected": len(it["facts"]),
                "matched": len(matched),
                "need": need,
                "recalled": (len(matched) >= need) if not it["negative"] else None,
                "emitted": len(facts),
                "missed": [
                    it["facts"][n]["claim"]
                    for n in range(len(it["facts"]))
                    if n not in matched
                ],
                "error": res.get("error"),
            }
        )
    pos_rows = [r for r in rows if not r["negative"]]
    neg_rows = [r for r in rows if r["negative"]]
    return {
        "items_recalled": sum(bool(r["recalled"]) for r in pos_rows),
        "items_total": len(pos_rows),
        "fact_recall": f"{tot_hit}/{tot_exp}",
        "emitted": emitted_all,
        "emitted_matched": emitted_matched,
        "strict_precision": round(emitted_matched / emitted_all, 3)
        if emitted_all
        else None,
        "negatives_clean": f"{len(neg_rows) - neg_dirty}/{len(neg_rows)}",
        "kind_accuracy_on_matched": f"{kind_ok}/{kind_n}",
        "deadline_accuracy_on_matched": f"{dl_ok}/{dl_n}",
        "rows": rows,
    }


def self_test(gold):
    """The scorer must give full marks to the gold's own claims and zero to nothing."""
    perfect = {
        "items": {
            i["id"]: {
                "facts": [
                    {
                        "subject": {"name": f["subject"]},
                        "predicate": f["kind"],
                        "text": f["claim"],
                        "value": f.get("amount"),
                        "validTo": f.get("deadline"),
                    }
                    for f in i["facts"]
                ]
            }
            for i in gold["items"]
        }
    }
    full = score(gold, perfect, 0.0)
    empty = score(gold, {"items": {}}, 0.0)
    ok = (
        full["items_recalled"] == full["items_total"]
        and full["strict_precision"] == 1.0
        and full["negatives_clean"].startswith("4/")
        and empty["items_recalled"] == 0
    )
    print(
        "self-test ok"
        if ok
        else f"self-test FAILED: {full['items_recalled']}/{full['items_total']} precision {full['strict_precision']}"
    )
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("result", nargs="?")
    ap.add_argument("--check-gold", action="store_true")
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--gold", default=str(HERE / "gold.json"))
    ap.add_argument("--min-salience", type=float, default=0.0)
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    gold = json.loads(Path(a.gold).read_text())
    if a.check_gold:
        problems = check_gold(gold)
        for p in problems:
            print("GOLD PROBLEM:", p)
        print("gold ok" if not problems else f"{len(problems)} problems")
        return 1 if problems else 0
    if a.self_test:
        return self_test(gold)
    if not a.result:
        ap.error("RESULT.json or --check-gold required")
    out = score(gold, json.loads(Path(a.result).read_text()), a.min_salience)
    if a.json:
        print(json.dumps(out, indent=1))
        return 0
    print(f"items recalled     {out['items_recalled']}/{out['items_total']}")
    print(f"fact recall        {out['fact_recall']}")
    print(
        f"strict precision   {out['strict_precision']}  ({out['emitted_matched']} of {out['emitted']} emitted facts matched a gold fact)"
    )
    print(f"negatives clean    {out['negatives_clean']}")
    print(f"kind accuracy      {out['kind_accuracy_on_matched']} (matched facts)")
    print(
        f"deadline accuracy  {out['deadline_accuracy_on_matched']} (matched facts with a gold deadline)"
    )
    print("\nmisses:")
    for r in out["rows"]:
        if r["negative"] and r["emitted"]:
            print(
                f"  FP  {r['id']}: {r['emitted']} fact(s) emitted for a negative item"
            )
        elif not r["negative"] and not r["recalled"]:
            print(
                f"  --  {r['id']} {r['matched']}/{r['expected']} (need {r['need']}){' ERROR ' + str(r['error']) if r['error'] else ''}: "
                + "; ".join(r["missed"])[:200]
            )
    return 0


if __name__ == "__main__":
    sys.exit(main())
