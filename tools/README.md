# OCR validation

Enable the local Paddle fallback by adding docker-compose.paddle.yml to the existing Compose command. Embedded text and Docling run first. Paddle runs after a Docling failure, before Tesseract. Model download occurs on the first request. The service processes 30 pages and up to 25 MB.

Install requests with Python and generate the existing synthetic fixtures. Compare two services using:

    python tools/evaluate-ocr.py examples/ocr-benchmark/fixtures --endpoint docling=http://127.0.0.1:5002 --endpoint paddle=http://127.0.0.1:YOUR_PORT --output ocr-report.json

Publish Paddle on an available local port, mapped to container port 8000. The comparison uses an identical label policy across providers and reports correct values, omissions, incorrect identifiers and elapsed seconds. Failed conversions count against the total. This isolates OCR quality from the main GED extractor benchmark.

The optional services/docling-candidate/Dockerfile builds a parser candidate with docling-parse 7.26.0. pip check blocks dependency conflicts. The regular Docling image stays pinned until this candidate passes validation. Compare elapsed time, memory and field accuracy on the same PDFs before switching. Docker stats samples current memory; record repeated samples to estimate peak memory.

Acceptance targets for 24 PDFs and 72 fields: at least 69 correct values, zero incorrect nonempty identifiers, and no failed PDFs. Parser target: 15% lower peak memory with unchanged field accuracy and at most 10% more time. These are targets, not measured results.
