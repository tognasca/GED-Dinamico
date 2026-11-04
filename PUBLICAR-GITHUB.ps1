# Executar dentro da pasta GED-Dinamico após instalar GitHub CLI e autenticar com: gh auth login
$ErrorActionPreference = 'Stop'
if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Instale o GitHub CLI: winget install --id GitHub.cli' }
gh auth status
if ($LASTEXITCODE -ne 0) { throw 'Execute: gh auth login' }
if (!(Test-Path .git)) { git init -b main }
git add .
git commit -m 'feat: bootstrap GED Dinamico OCR e modelos documentais'
if ($LASTEXITCODE -ne 0) { Write-Warning 'Verifique o status do Git antes de publicar.' }
# Este comando cria um repositorio PRIVADO se ele ainda nao existir.
gh repo create tognasca/GED-Dinamico --private --source . --remote origin --push
if ($LASTEXITCODE -ne 0) { throw 'Verifique se o repositorio ja existe ou se a conta possui permissao.' }
Write-Host 'Projeto publicado: https://github.com/tognasca/GED-Dinamico' -ForegroundColor Green
