"""Generate twelve synthetic policies and their ground truth."""
import argparse
import json
import pymupdf
from pathlib import Path
from tempfile import TemporaryDirectory

from PIL import Image, ImageFilter
from reportlab.pdfgen import canvas

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--pilot", action="store_true", help="Add controls with absent fields.")
args = parser.parse_args()
output = Path(__file__).with_name("fixtures-pilot" if args.pilot else "fixtures")
output.mkdir(exist_ok=True)
manifest = {}
for i in range(14 if args.pilot else 12):
    name = f"apolice-{i + 1:02}"
    fields = {"numero_apolice": f"202600{i + 10}", "placa": f"TST1A{i + 10}",
              "inicio_vigencia": f"{i + 1:02}/11/2026"}
    if i >= 12:
        name = f"scan-sem-placa-{i - 11:02}"
        fields["placa"] = ""
    manifest[name + ".pdf"] = fields
    layout = i % 3
    with TemporaryDirectory() as scratch:
        path = Path(scratch) / "source.pdf"
        pdf = canvas.Canvas(str(path))
        pdf.setFont("Helvetica-Bold", 16)
        pdf.drawString(50, 790, "Apólice de teste — Seguradora Fictícia")
        pdf.setFont("Helvetica", 12)
        for row, (label, value) in enumerate(zip(
            ("Número da apólice", "Placa", "Início da vigência"), fields.values()
        )):
            if not value:
                continue
            if layout == 0:
                pdf.drawString(50, 730 - row * 28, f"{label}: {value}")
            elif layout == 1:
                pdf.drawString(50, 730 - row * 28, label)
                pdf.drawString(300, 730 - row * 28, value)
            else:
                pdf.drawString(50 + row * 165, 730, label)
                pdf.drawString(50 + row * 165, 706, value)
        if layout == 2:
            for x in (45, 210, 375, 540):
                pdf.line(x, 695, x, 747)
            for y in (695, 720, 747):
                pdf.line(45, y, 540, y)
        pdf.save()
        target = output / (name + ".pdf")
        if i < 6:
            target.write_bytes(path.read_bytes())
        else:
            with pymupdf.open(path) as source:
                raster = source[0].get_pixmap(dpi=150, alpha=False)
                scan = Image.frombytes("RGB", (raster.width, raster.height), raster.samples)
                if i >= 9:
                    scan = scan.rotate(0.7, fillcolor="white").filter(ImageFilter.GaussianBlur(0.7))
                scan.save(target, resolution=150)
(output / "expected.json").write_text(json.dumps(manifest, indent=2) + "\n")
