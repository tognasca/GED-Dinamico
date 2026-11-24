"""Generate twelve synthetic policies and their ground truth."""
import argparse
import json
import re
import pymupdf
from pathlib import Path
from tempfile import TemporaryDirectory

from PIL import Image, ImageFilter
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--pilot", action="store_true", help="Add controls with absent fields.")
parser.add_argument("--cmap-font", type=Path, help="TrueType font to add six digital CMap cases to the pilot.")
args = parser.parse_args()
if args.cmap_font and not args.pilot:
    parser.error("--cmap-font requires --pilot")
if args.cmap_font:
    pdfmetrics.registerFont(TTFont("PilotCMap", str(args.cmap_font)))
output = Path(__file__).with_name("fixtures-pilot" if args.pilot else "fixtures")
output.mkdir(exist_ok=True)
manifest = {}
for i in range(20 if args.cmap_font else 14 if args.pilot else 12):
    name = f"apolice-{i + 1:02}"
    fields = {"numero_apolice": f"202600{i + 10}", "placa": f"TST1A{i + 10}",
              "inicio_vigencia": f"{i + 1:02}/11/2026"}
    cmap_case = i >= 14
    if cmap_case:
        name = f"cmap-{i - 13:02}"
    elif i >= 12:
        name = f"scan-sem-placa-{i - 11:02}"
        fields["placa"] = ""
    manifest[name + ".pdf"] = fields
    layout = i % 3
    with TemporaryDirectory() as scratch:
        path = Path(scratch) / "source.pdf"
        pdf = canvas.Canvas(str(path))
        pdf.setFont("Helvetica-Bold", 16)
        pdf.drawString(50, 790, "Apólice de teste — Seguradora Fictícia")
        pdf.setFont("PilotCMap" if cmap_case else "Helvetica", 12)
        for row, (label, value) in enumerate(zip(
            ("Número da apólice", "Placa", "Início da vigência"), fields.values()
        )):
            if not value:
                continue
            if layout == 0 or cmap_case:
                pdf.drawString(50, 730 - row * 28, f"{label}: {value}")
            elif layout == 1:
                pdf.drawString(50, 730 - row * 28, label)
                pdf.drawString(300, 730 - row * 28, value)
            else:
                pdf.drawString(50 + row * 165, 730, label)
                pdf.drawString(50 + row * 165, 706, value)
        if layout == 2 and not cmap_case:
            for x in (45, 210, 375, 540):
                pdf.line(x, 695, x, 747)
            for y in (695, 720, 747):
                pdf.line(45, y, 540, y)
        pdf.save()
        target = output / (name + ".pdf")
        if cmap_case:
            with pymupdf.open(path) as source:
                found = False
                for font in source.get_page_fonts(0):
                    kind, ref = source.xref_get_key(font[0], "ToUnicode")
                    if kind != "xref":
                        continue
                    xref = int(ref.split()[0])
                    cmap = source.xref_stream(xref)
                    cmap, changed = re.subn(
                        rb"1 begincodespacerange\s+<00> <[0-9A-Fa-f]{2}>",
                        b"2 begincodespacerange\n<00> <7F>\n<80> <FF>", cmap,
                    )
                    if changed:
                        source.update_stream(xref, cmap)
                        found = True
                if not found:
                    raise RuntimeError("O PDF de teste precisa de um mapa ToUnicode de um byte.")
                source.save(target)
        elif i < 6:
            target.write_bytes(path.read_bytes())
        else:
            with pymupdf.open(path) as source:
                raster = source[0].get_pixmap(dpi=150, alpha=False)
                scan = Image.frombytes("RGB", (raster.width, raster.height), raster.samples)
                if i >= 9:
                    scan = scan.rotate(0.7, fillcolor="white").filter(ImageFilter.GaussianBlur(0.7))
                scan.save(target, resolution=150)
(output / "expected.json").write_text(json.dumps(manifest, indent=2) + "\n")
if args.cmap_font:
    cmap_manifest = {name: fields for name, fields in manifest.items() if name.startswith("cmap-")}
    (output / "expected-cmap.json").write_text(json.dumps(cmap_manifest, indent=2) + "\n")
