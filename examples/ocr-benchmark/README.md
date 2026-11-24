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

## Piloto de regressão do núcleo Docling

O piloto roda em dois ambientes Python isolados e usa o mesmo extrator .NET
do GED em ambos. Compare 2.142.1 (a versão do núcleo listada na release do
Docling Serve 1.38.2) e 2.151.1. A comparação exercita o pipeline PDF padrão
local em CPU com Tesseract CLI, não o endpoint Serve nem o caminho VLM.
Valide a imagem Serve separadamente antes de trocar o serviço do GED.

Em PowerShell, com Python 3.12, uv, SDK .NET 10 e Tesseract com `por` e `eng`,
execute na pasta deste benchmark:

```powershell
tesseract --list-langs
uv run --no-project --with pillow --with reportlab --with pymupdf python generate.py --pilot

uv venv --python 3.12 .venv-baseline
uv venv --python 3.12 .venv-candidate
$baseline = '.\.venv-baseline\Scripts\python.exe'
$candidate = '.\.venv-candidate\Scripts\python.exe'
foreach ($python in @($baseline, $candidate)) {
    uv pip install --python $python --index https://download.pytorch.org/whl/cpu 'torch==2.8.0+cpu' 'torchvision==0.23.0+cpu'
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao instalar o runtime CPU' }
}
uv pip install --python $baseline 'docling==2.142.1'
uv pip install --python $candidate 'docling==2.151.1'
& $baseline capture_docling.py fixtures-pilot capture-baseline.json
if ($LASTEXITCODE -ne 0) { throw 'Falha na captura de referência' }
& $candidate capture_docling.py fixtures-pilot capture-candidate.json
if ($LASTEXITCODE -ne 0) { throw 'Falha na captura candidata' }
dotnet run --project Benchmark.csproj -- --compare-docling fixtures-pilot/expected.json capture-baseline.json capture-candidate.json
```

No Linux, use `.venv-baseline/bin/python` e `.venv-candidate/bin/python`.
A primeira execução precisa baixar os pesos dos modelos. Gere os PDFs
**uma vez por comparação**. As capturas registram hash, versão, idiomas e
tempo por PDF, e recusam sobrescrever arquivos existentes. Use novos nomes
para repetir o teste. Os JSONs de captura contêm texto de documentos,
portanto mantenha-os locais se testar arquivos reais autorizados.

A triagem usa 14 PDFs sintéticos (três layouts, digitais e escaneados,
incluindo dois escaneados sem placa) e 42 campos esperados. O comparador
exige pelo menos 95% de correspondência exata após normalizar espaços,
Unicode e datas, nenhum campo antes correto perdido, nenhum número de
apólice errado preenchido e zero preenchimentos de campos ausentes.
O p95 de conversão exclui a inicialização dos pesos. Código de saída
`0` aprova a triagem, `1` reprova e `2` indica entradas inválidas.
A aprovação é um primeiro filtro. A promoção depende de validar a
imagem do serviço, documentos autorizados representativos e a conferência
no React. Contratos, notas fiscais e LangExtract ficam para uma etapa
separada, após estabelecer a referência para apólices.

## Regressão de fontes ToUnicode

Use `python generate.py --pilot --cmap-font CAMINHO_DA_FONTE.ttf` para
adicionar seis PDFs digitais com fonte incorporada e duas faixas no CMap.
O gabarito completo passa a ter 60 campos. Use uma fonte Latin TrueType,
como DejaVu Sans, com os caracteres acentuados do exemplo.

Em dois ambientes Python separados, instale respectivamente
`docling-parse==7.27.0` e `docling-parse==7.27.1`. Em cada ambiente, execute
`python capture_cmap.py fixtures-pilot capture-baseline-cmap.json`, trocando
o nome da segunda saída por `capture-candidate-cmap.json`. Compare usando:

```sh
dotnet run --project Benchmark.csproj -- --compare-docling fixtures-pilot/expected-cmap.json capture-baseline-cmap.json capture-candidate-cmap.json
```

Essa triagem usa o parser diretamente e avalia 18 campos sem carregar
modelos de OCR. No primeiro teste, as duas versões alcançaram 18/18. O
conjunto sintético não reproduziu perda de campo na versão anterior.

Para avaliar os 60 campos, use `capture_docling.py` e `expected.json`.
Fixe o mesmo núcleo Docling nos dois ambientes e altere apenas
`docling-parse`, desde que o resolvedor aceite ambas as versões. A captura
registra as versões do núcleo e do parser. O critério do piloto ampliado é
57/60 ou mais, zero preenchimentos indevidos, preservação dos acertos
anteriores e pelo menos um erro corrigido nos casos novos para demonstrar
ganho. A aprovação do comparador nos 18 campos sozinha indica paridade.
