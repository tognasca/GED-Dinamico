import { useEffect, useState } from 'react';
import { API_URL, DEMO_TENANT, type GedDocument } from './api';

export function useDocumentFile(document?: GedDocument) {
  const [file, setFile] = useState<{ id: string; url?: string; error?: string }>();
  useEffect(() => {
    setFile(undefined);
    if (!document) return;
    const controller = new AbortController();
    let url: string | undefined;
    void fetch(`${API_URL}/api/documents/${document.id}/file`, {
      headers: { 'X-Tenant-Id': DEMO_TENANT }, signal: controller.signal,
    }).then(response => {
      if (!response.ok) throw new Error();
      return response.blob();
    }).then(blob => {
      if (controller.signal.aborted) return;
      url = URL.createObjectURL(blob);
      setFile({ id: document.id, url });
    }).catch(() => {
      if (!controller.signal.aborted) setFile({ id: document.id, error: 'Não foi possível abrir o original. Tente novamente mais tarde.' });
    });
    return () => { controller.abort(); if (url) URL.revokeObjectURL(url); };
  }, [document?.id]);
  return file?.id === document?.id ? file : undefined;
}

export default function DocumentPreview({ document, url, error }: {
  document: GedDocument; url?: string; error?: string;
}) {
  if (error) return <p role="alert">{error}</p>;
  if (!url) return <p role="status">Abrindo original…</p>;
  if (document.contentType === 'application/pdf')
    return <iframe className="teaching-original" title={`Original de ${document.fileName}`} src={url}/>;
  if (document.contentType.startsWith('image/') && document.contentType !== 'image/tiff')
    return <img className="teaching-original" src={url} alt={document.fileName}/>;
  return <div><a href={url} download={document.fileName}>Baixar original para conferir</a>
    <p>Texto reconhecido para consulta</p><pre className="teaching-text">{document.ocrText}</pre></div>;
}
