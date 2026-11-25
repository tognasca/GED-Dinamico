"""Capture an isolated Docling run over the synthetic benchmark fixtures."""
import argparse
from importlib.metadata import version
from hashlib import sha256
import json
from pathlib import Path
from time import perf_counter

from docling.datamodel.base_models import ConversionStatus
from docling.datamodel.pipeline_options import (
    AcceleratorDevice, AcceleratorOptions, PdfPipelineOptions, TesseractCliOcrOptions,
)
from docling.document_converter import DocumentConverter, InputFormat, PdfFormatOption


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--languages", nargs="+", default=["por", "eng"])
    args = parser.parse_args()
    manifest = json.loads((args.fixtures / "expected.json").read_text(encoding="utf-8"))
    if not manifest:
        parser.error("O conjunto de PDFs está vazio.")
    options = PdfPipelineOptions(
        accelerator_options=AcceleratorOptions(device=AcceleratorDevice.CPU, num_threads=4),
        ocr_options=TesseractCliOcrOptions(lang=args.languages),
    )
    converter = DocumentConverter(format_options={
        InputFormat.PDF: PdfFormatOption(pipeline_options=options)
    })
    # Prime the pipeline so model loading is excluded from per-document latency.
    converter.initialize_pipeline(InputFormat.PDF)
    samples = {}
    for name in manifest:
        path = args.fixtures / name
        fingerprint = sha256(path.read_bytes()).hexdigest()
        start = perf_counter()
        result = converter.convert(path, page_range=(1, 30))
        elapsed_ms = round((perf_counter() - start) * 1000)
        if result.status != ConversionStatus.SUCCESS:
            raise RuntimeError(f"Conversão incompleta: {name} ({result.status})")
        value = result.document.export_to_text()
        if not value.strip():
            raise RuntimeError(f"Documento sem texto: {name}")
        samples[name] = {
            "text": value, "milliseconds": elapsed_ms, "sha256": fingerprint,
            "structure": result.document.export_to_dict(),
        }
        print(f"{name}: {elapsed_ms} ms", flush=True)
    payload = {
        "version": f"Docling {version('docling')} / parser {version('docling-parse')}",
        "languages": args.languages,
        "documents": samples,
    }
    with args.output.open("x", encoding="utf-8") as report:
        json.dump(payload, report, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
