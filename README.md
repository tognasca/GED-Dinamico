# GED Dinâmico — OCR, modelos documentais e indexação

MVP de engenharia para **GED com OCR real**, modelos de campos configuráveis, revisão humana, lotes e estrutura de localização física. Stack: **ASP.NET Core 10 / C#**, **React 19 + TypeScript**, **PostgreSQL 16**, **Tesseract OCR**, **Poppler** e **storage local substituível**.

> **Estado do projeto:** base funcional de desenvolvimento, não produto pronto para produção. Não há autenticação real, autorização, fila assíncrona, migrações versionadas, versionamento de modelos ou migração real de storage. Essas funções estão documentadas no roadmap. **Não exponha este serviço na Internet** nem use dados pessoais reais antes de implementar controles de segurança.

## Rodar no Windows com Docker Desktop

Pré-requisitos: Docker Desktop, Node.js 22+ e npm. O Docker instala o OCR em português/inglês e o conversor de PDF.

```powershell
git clone https://github.com/tognasca/GED-Dinamico.git
cd GED-Dinamico
Copy-Item .env.example .env
# Edite GED_DB_PASSWORD no .env ANTES de iniciar

docker compose up -d --build
cd frontend
npm install
npm run dev
```

- **Frontend:** http://localhost:5173
- **API Swagger:** http://localhost:5096/swagger
- **PostgreSQL:** `127.0.0.1:5435` (apenas loopback).

O `git clone` acima pressupõe que o repositório novo tenha sido criado e os arquivos publicados. Caso use o ZIP, extraia e execute os comandos a partir de `docker compose up -d --build`.

### Fluxo de demonstração (cheque)

1. Na **Central de digitalização**, importe uma imagem legível, PDF ou `examples/cheque-exemplo.txt` (TXT serve apenas para testar a indexação sem instalação de OCR no navegador).
2. Confira o texto e os campos detectados; informe nome do modelo, por exemplo **Cheque**, e palavras-chave, por exemplo `cheque,banco,favorecido`.
3. Ajuste os campos, clique **Salvar modelo dinâmico** e depois **Aplicar**.
4. Confira os valores extraídos e clique **Confirmar indexação**.
5. Envie outro documento com o mesmo tipo. Com palavras-chave inequívocas, o modelo será selecionado automaticamente e ficará pendente de conferência.
6. Na aba **Arquivo físico**, configure localizações hierárquicas e vincule caixas a locais.

**O que é automático neste MVP:** OCR real via Tesseract, sugestão de campos conhecidos via regras, seleção de modelo por palavras-chave e extração conforme campos do modelo. Todos os dados extraídos ainda exigem confirmação. Não há classificação semântica por LLM, treinamento automático ou editor visual por coordenadas.

## API resumida

| Método | Endpoint | Propósito |
|---|---|---|
| GET | `/api/types` | Listar modelos |
| POST | `/api/types` | Criar modelo e campos dinâmicos |
| POST | `/api/documents/upload` | Upload + OCR + sugestões + tentativa de aplicar modelo |
| GET | `/api/documents` | Listar/buscar documentos |
| GET | `/api/documents/{id}` | Documento e dados extraídos |
| POST | `/api/documents/{id}/apply-template` | Extrair campos com modelo selecionado |
| POST | `/api/documents/{id}/confirm` | Revisar e indexar |
| GET | `/api/documents/{id}/file` | Abrir arquivo do storage local |
| GET/POST | `/api/batches` | Gestão inicial de lotes |
| POST | `/api/batches/{id}/close` | Fechar lote totalmente indexado |
| GET/POST | `/api/locations` | Estrutura física hierárquica |
| GET/POST | `/api/boxes` | Caixas |
| GET | `/api/boxes/{id}/route` | Trajeto físico completo |
| POST | `/api/documents/{id}/box` | Associar documento a caixa |

**Tenant de demonstração:** a UI envia `X-Tenant-Id: 11111111-1111-4111-8111-111111111111`. **Este cabeçalho NÃO é segurança e pode ser falsificado.** Substituir por JWT/OIDC com tenant resolvido exclusivamente de claims verificadas é bloqueante para produção.

## Decisões de arquitetura

- Modelo documental em `DocumentType` com `FieldsJson` e `KeywordsJson` (`jsonb` no PostgreSQL). Documentos persistem `ValuesJson` e `SuggestionsJson`; modelo e campos não são hard-coded por tipo de arquivo.
- `IStorageProvider` abstrai o armazenamento. Inicialmente `LocalStorageProvider`, usa IDs opacos e SHA-256; a próxima fase adiciona provedores S3/MinIO/Azure/SMB e migração verificada.
- `IOcrProvider` permite trocar Tesseract por mecanismos avançados. Arquivos PDF são renderizados pelo `pdftoppm` (até 30 páginas na versão inicial).
- Extração híbrida inicial: regex/âncora e palavras-chave. O sistema **não inventa** confiança de IA; resultados de regras usam score heurístico e são revisados.
- Arquivo físico: nós de localização com `ParentId`, permitindo níveis ilimitados; caixas referenciam o nó físico.

## Limitações relevantes

- `Database.EnsureCreated()` é apenas bootstrapping; substituir por migrations do EF Core e testes antes de dados reais.
- OCR ocorre durante a requisição de upload: mover para fila + worker durável para produção e grandes lotes.
- Regex por campo deve ser configurada com cuidado; há timeout por expressão, mas configuração administrativa requer controles adicionais.
- Não há verificação de assinatura/magic bytes nem proteção antivírus no upload, somente lista MIME, tamanho e armazenamento opaco.
- Busca atual é simples em nome/OCR; índices dedicados, Full Text Search e consulta em JSONB são próximas fases.
- Cadastro de modelos não tem editor completo nem edição/versionamento; criação e aplicação estão disponíveis.
- A API de exemplo se destina a uso local com conteúdo fictício. Em produção, exigir controle de acesso, auditoria, LGPD, retenção, criptografia e backups.

## Roadmap

Veja [docs/ROADMAP.md](docs/ROADMAP.md) e [docs/ARQUITETURA.md](docs/ARQUITETURA.md).

## Publicar em repositório próprio no GitHub

Opcionalmente instale GitHub CLI com `winget install --id GitHub.cli`, autentique com `gh auth login`, navegue até a pasta extraída e execute `./PUBLICAR-GITHUB.ps1`. Esse script cria `tognasca/GED-Dinamico` como **privado** e faz o primeiro push. Não cole tokens no terminal nem nos arquivos do repositório. A criação do repositório não pôde ser executada automaticamente por esta conexão.

O workflow em `.github/workflows/ci.yml` fará builds do backend e frontend depois do push. O build **ainda não foi validado** neste ambiente por ausência do SDK .NET e das dependências npm.
