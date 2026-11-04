# Roadmap — da base técnica ao GED SaaS empresarial

## Entregue na base inicial

- API .NET 10 e frontend React responsivo.
- PostgreSQL 16 via Docker Compose.
- OCR de imagens com Tesseract e PDF via Poppler/Tesseract.
- Sugestão inicial de campos, modelos configuráveis por metadados e extração por âncora/regex.
- Aplicação explícita de modelos, seleção simples por palavras-chave e revisão dos valores.
- Armazenamento local com identificador lógico e SHA-256.
- Lotes básicos, estrutura hierárquica física, caixas, rotas e associação documento/caixa.

## Prioridade 0 — segurança e confiabilidade

- Autenticação JWT/OIDC, perfis RBAC e tenancy por claims assinadas.
- Auditoria de alterações, solicitações e downloads; nenhuma informação sensível em logs.
- Migrations EF Core; testes de integração; políticas de backup/restore.
- Validação de tipo por conteúdo, antivírus, limites por páginas e política de retenção.
- CORS restrito, HTTPS e proteção contra abuso.

## Prioridade 1 — motor documental realmente dinâmico

- Editor visual de campos em PDF/imagem, seleção por bounding-box e âncora semântica.
- Classificador documental por IA, confirmação de modelos novos, detecção de ambiguidade.
- Versionamento imutável de DocumentTemplateVersion e histórico de extrações.
- Campos customizados, validações CPF/CNPJ, datas e moeda; tabelas/checkboxes.
- Schema dinâmico e indexação de metadados JSONB com índices adequados.
- Worker de OCR com fila durável, jobs idempotentes e SignalR para progresso.

## Prioridade 2 — operação GED empresarial

- Captura por scanner TWAIN/WIA via agente Windows, câmera e importação em lote.
- Separação de páginas/documentos, deduplicação, correção de perspectiva.
- Processos de aprovação, alçadas, revisores e SLA.
- Localização física com QR Code e histórico de custódia / movimentação.
- Pesquisa avançada e full-text via PostgreSQL (ou OpenSearch quando justificar).
- Relatórios de desempenho e precisão por modelo/campo/provedor.

## Prioridade 3 — infraestrutura e produto

- S3/MinIO/Azure Blob e armazenamento SMB por tenant.
- Migração verificada com checksums, retomada e apontamento atômico.
- SaaS multi-tenant com quotas e configurações por organização.
- PWA offline e aplicativo Windows com agente de scanner.
- Deploy Kubernetes/opcional e pipeline CI/CD.
