$ErrorActionPreference = 'Stop'
Write-Host 'Iniciando GED Dinamico (API, PostgreSQL, OCR)...' -ForegroundColor Cyan
if (!(Test-Path .env)) { Copy-Item .env.example .env; Write-Warning 'Edite GED_DB_PASSWORD no .env antes de expor a outro usuario.' }
docker compose up -d --build
Write-Host 'API: http://localhost:9192/swagger' -ForegroundColor Green
Write-Host 'Inicie o frontend em outra janela:' -ForegroundColor Yellow
Write-Host 'cd frontend; npm install; npm run dev'
