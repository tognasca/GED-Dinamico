"""Compare compatible OCR endpoints using GED fixtures and field-specific scoring."""
import argparse
import json
import re
import time
from pathlib import Path
import requests

parser = argparse.ArgumentParser()
parser.add_argument("fixtures", type=Path)
parser.add_argument("--endpoint", action="append", required=True, help="name=http://host:port")
parser.add_argument("--output", type=Path, default=Path("ocr-report.json"))
args = parser.parse_args()
truth = json.loads((args.fixtures / "expected.json").read_text())
report = []
for service in args.endpoint:
    name, base = service.split("=", 1)
    for filename, expected in truth.items():
        started = time.monotonic()
        row = {"engine": name, "file": filename, "fields": {}}
        try:
            with (args.fixtures / filename).open("rb") as source:
                response = requests.post(base.rstrip("/") + "/v1/convert/file",
                    files={"files": (filename, source, "application/pdf")},
                    data={"to_formats": "text", "do_ocr": "true"}, timeout=180)
            response.raise_for_status()
            payload = response.json()
            if payload.get("status") != "success":
                raise ValueError("Incomplete OCR")
            text = payload["document"]["text_content"]
            # Explicit label boundaries prevent one field consuming the next label.
            labels = {"numero_apolice": r"(?:Número da apólice|Apólice)",
                      "placa": r"Placa", "inicio_vigencia": r"Início da vigência"}
            formats = {"numero_apolice": r"[0-9]{6,}", "placa": r"[A-Z]{3}[0-9][A-Z0-9][0-9]{2}",
                       "inicio_vigencia": r"[0-9]{2}/[0-9]{2}/[0-9]{4}"}
            for key, gold in expected.items():
                candidates = set(re.findall(labels[key] + r"\s*[:\-]?\s*(" + formats[key] + r")", text, re.I))
                found = next(iter(candidates)) if len(candidates) == 1 else ""
                row["fields"][key] = {"expected": gold, "value": found,
                    "correct": found.casefold() == gold.casefold(),
                    "omitted": not found, "incorrect": bool(found) and found.casefold() != gold.casefold()}
        except Exception as error:
            row["error"] = str(error)
        row["seconds"] = round(time.monotonic() - started, 3)
        report.append(row)
args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
for service in args.endpoint:
    name = service.split("=", 1)[0]
    rows = [r for r in report if r["engine"] == name]
    fields = [f for r in rows for f in r["fields"].values()]
    total = sum(len(v) for v in truth.values())
    correct = sum(f["correct"] for f in fields)
    incorrect_ids = sum(f["incorrect"] for r in rows for k, f in r["fields"].items() if k != "inicio_vigencia")
    print(f"{name}: {correct}/{total} correct; {incorrect_ids} incorrect identifiers; {sum('error' in r for r in rows)} failed PDFs")
