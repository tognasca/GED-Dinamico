"""Normalize known policy layouts in a synthetic Docling capture."""
import argparse
import json
import re
import unicodedata
from pathlib import Path

LABELS = ("Número da apólice", "Placa", "Início da vigência")
KEYS = ("numero da apolice", "placa", "inicio da vigencia")
FORMATS = (r"[0-9]{6,}", r"[A-Z]{3}[0-9][A-Z0-9][0-9]{2}", r"[0-9]{2}/[0-9]{2}/[0-9]{4}")


def normalize(text):
    folded = "".join(c for c in unicodedata.normalize("NFD", text.lower())
                     if not unicodedata.combining(c))
    candidates = {key: set() for key in KEYS}
    for key, pattern in zip(KEYS, FORMATS):
        for value in re.findall(rf"\b{key}[ \t]*:?[ \t]*({pattern.lower()})(?!\w)", folded):
            candidates[key].add(value.upper())

    lines = [line.strip() for line in folded.splitlines() if line.strip()]
    for index in range(len(lines) - 2):
        header, divider, row = lines[index:index + 3]
        if not all(line.startswith("|") and line.endswith("|") for line in (header, divider, row)):
            continue
        keys, separators, values = ([cell.strip() for cell in line[1:-1].split("|")]
                                    for line in (header, divider, row))
        if len(keys) == len(separators) == len(values) and all(re.fullmatch(r":?-{3,}:?", s) for s in separators):
            for key, value in zip(keys, values):
                if key in candidates and re.fullmatch(FORMATS[KEYS.index(key)], value.upper()):
                    candidates[key].add(value.upper())

    # Accept a complete label/value block only when all positions validate.
    for size in (2, 3):
        for index in range(len(lines) - 2 * size + 1):
            keys = [label.rstrip(":") for label in lines[index:index + size]]
            values = [value.upper() for value in lines[index + size:index + 2 * size]]
            if len(set(keys)) == size and all(
                key in candidates and re.fullmatch(FORMATS[KEYS.index(key)], value)
                for key, value in zip(keys, values)
            ):
                for key, value in zip(keys, values):
                    candidates[key].add(value)

    return "\n".join(f"{label}: {next(iter(candidates[key]))}" for key, label in zip(KEYS, LABELS)
                     if len(candidates[key]) == 1) or "Nenhum campo reconhecido"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    capture = json.loads(args.source.read_text(encoding="utf-8"))
    capture["version"] += " + normalização de apólice"
    for sample in capture["documents"].values():
        sample["text"] = normalize(sample["text"])
    with args.output.open("x", encoding="utf-8") as report:
        json.dump(capture, report, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
