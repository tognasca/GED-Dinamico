"""Capture text from the six CMap fixtures without loading OCR models."""
import argparse
from hashlib import sha256
from importlib.metadata import version
import json
from pathlib import Path
from time import perf_counter

from docling_parse.pdf_parser import DoclingPdfParser


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("fixtures", type=Path)
    cli.add_argument("output", type=Path)
    args = cli.parse_args()
    expected = json.loads((args.fixtures / "expected-cmap.json").read_text(encoding="utf-8"))
    parser = DoclingPdfParser()
    samples = {}
    for name in expected:
        path = args.fixtures / name
        fingerprint = sha256(path.read_bytes()).hexdigest()
        start = perf_counter()
        document = parser.load(path)
        try:
            text = "\n".join(cell.text for _, page in document.iterate_pages()
                             for cell in page.textline_cells)
        finally:
            document.unload()
        samples[name] = {"text": text, "milliseconds": round((perf_counter() - start) * 1000),
                         "sha256": fingerprint}
    with args.output.open("x", encoding="utf-8") as report:
        json.dump({"version": f"docling-parse {version('docling-parse')}",
                   "languages": [], "documents": samples}, report, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
