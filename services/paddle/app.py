"""Optional local PaddleOCR service using a Docling-compatible text response."""
import asyncio
import os
import tempfile
import threading
from pathlib import Path

import pymupdf
from fastapi import FastAPI, File, UploadFile
from paddleocr import PaddleOCR

app = FastAPI()
engine = None
lock = threading.Lock()


def recognize(data):
    global engine
    with lock:
        if engine is None:
            engine = PaddleOCR(lang="pt", use_doc_orientation_classify=False,
                               use_doc_unwarping=False, use_textline_orientation=False)
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.pdf"
            source.write_bytes(data)
            texts = []
            with pymupdf.open(source) as document:
                for index, page in enumerate(document):
                    if index >= 30:
                        break
                    image = Path(directory) / f"{index}.png"
                    page.get_pixmap(dpi=200).save(image)
                    for result in engine.predict(str(image)):
                        payload = result.json
                        if isinstance(payload, str):
                            import json
                            payload = json.loads(payload)
                        texts.extend(payload.get("res", payload).get("rec_texts", []))
            return "\n".join(texts)


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/v1/convert/file")
async def convert(files: UploadFile = File(...)):
    data = await files.read(25 * 1024 * 1024 + 1)
    if len(data) > 25 * 1024 * 1024:
        from fastapi import HTTPException
        raise HTTPException(413, "PDF exceeds 25 MB")
    text = await asyncio.to_thread(recognize, data)
    return {"status": "success", "document": {"text_content": text, "json_content": {}}}
