# Comparação de extração de apólices

Na pasta `examples/ocr-benchmark`, com Python e SDK .NET 10:

```sh
python -m pip install Pillow reportlab pymupdf
python generate.py
dotnet run --project Benchmark.csproj
```

O teste usa um Docling Serve acessível em `http://localhost:5001`. Configure
`DOCLING_URL` para outro endereço. O teste usa o motor de OCR padrão do Docling.

São 12 PDFs sintéticos, seis digitais e seis escaneados, em três layouts.
Cada PDF é enviado uma vez ao Docling. A comparação usa o mesmo extrator .NET
do GED nas duas abordagens. A segunda tenta recuperar campos vazios a partir de
células vizinhas em tabelas reconhecidas.

O teste passa com pelo menos 33 de 36 campos corretos, melhora sobre o texto
simples e nenhum número de apólice incorreto entre os valores preenchidos.
O teste avalia a extração isoladamente. O upload, o banco e o React devem ser
validados separadamente antes de habilitar o recurso em produção.
