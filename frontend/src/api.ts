
// ==========================================================
// GED DINÂMICO - CLIENTE API
// React + Vite + ASP.NET Core
// ==========================================================

// Usa o proxy configurado no vite.config.ts.
// As requisições serão enviadas para /api/... na porta 5173,
// e o Vite encaminhará para a API .NET na porta 5095.
export const API_URL = '';

// Tenant de demonstração (somente desenvolvimento).
export const DEMO_TENANT = '11111111-1111-4111-8111-111111111111';

// ==========================================================
// TIPOS
// ==========================================================

export type Field = {
  key: string;
  label: string;
  dataType: string;
  required: boolean;
  regex?: string | null;
  anchor?: string | null;
};

export type DocumentType = {
  id: string;
  name: string;
  version: number;
  active: boolean;
  keywords: string[];
  fields: Field[];
};

export type FieldResult = {
  value: string;
  confidence: number;
  source: string;
};

export type Suggestion = {
  key: string;
  label: string;
  dataType: string;
  value: string;
  suggested: boolean;
};

export type GedDocument = {
  id: string;
  fileName: string;
  contentType: string;
  fileSize: number;
  status: string;
  ocrStatus: string;
  ocrText: string;
  documentTypeId: string | null;
  batchId: string | null;
  createdAt: string;
  suggestions: Suggestion[] | { error?: string };
  values: Record<string, FieldResult>;
};

export type Batch = {
  id: string;
  name: string;
  status: string;
  createdAt: string;
};

// ==========================================================
// FUNÇÃO CENTRAL DE COMUNICAÇÃO COM A API
// ==========================================================

export async function api<T>(
  path: string,
  options: RequestInit = {}
): Promise<T> {

  // Aceita "/types" ou "types", sem duplicar "/api".
  const normalizedPath = path.startsWith('/')
    ? path
    : `/${path}`;

  const endpoint = `${API_URL}/api${normalizedPath}`;

  // FormData precisa que o navegador configure automaticamente
  // o Content-Type com o boundary correto.
  const isFormData = options.body instanceof FormData;

  const headers = new Headers(options.headers);

  headers.set('X-Tenant-Id', DEMO_TENANT);

  if (!isFormData && options.body != null && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }

  const response = await fetch(endpoint, {
    ...options,
    headers,
  });

  // ========================================================
  // TRATAMENTO DE ERROS HTTP
  // ========================================================

  if (!response.ok) {
    let error = `Erro HTTP ${response.status}`;

    try {
      const body = await response.json() as {
        error?: string;
        message?: string;
        title?: string;
      };

      error = body.error || body.message || body.title || error;
    } catch {
      // Mantém a mensagem HTTP quando não houver JSON.
    }

    throw new Error(error);
  }

  // Respostas sem conteúdo.
  if (response.status === 204) {
    return undefined as T;
  }

  // ========================================================
  // VALIDAÇÃO DO RETORNO
  // ========================================================

  const contentType = response.headers.get('content-type') ?? '';

  if (!contentType.toLowerCase().includes('application/json') &&
      !contentType.toLowerCase().includes('+json')) {
    throw new Error(
      `A API retornou conteúdo inválido em ${endpoint}. ` +
      `Esperado JSON, recebido: ${contentType || 'desconhecido'}. ` +
      `Verifique o proxy do Vite.`
    );
  }

  return await response.json() as T;
}
