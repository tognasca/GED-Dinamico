import { useState } from 'react';
import { api, type GedDocument, type Field } from './api';

export default function LayoutRuleEditor({ document, documents, field, version, onSaved }: {
  document: GedDocument;
  documents: GedDocument[];
  field: Field;
  version: number;
  onSaved: () => void;
}) {
  const [marker, setMarker] = useState('');
  const [anchor, setAnchor] = useState('');
  const [testId, setTestId] = useState('');
  const [expected, setExpected] = useState('');
  const [preview, setPreview] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const testFiles = documents.filter(item => item.id !== document.id && item.ocrStatus === 'Completed');

  async function submit(publish: boolean) {
    setBusy(true);
    setMessage('');
    if (!publish) setPreview(undefined);
    try {
      const result = await api<{ value: string }>(`/types/${document.documentTypeId}/layout-rule`, {
        method: 'POST',
        body: JSON.stringify({
          version, documentId: document.id, testDocumentId: testId,
          fieldKey: field.key, marker, anchor, publish, expected,
        }),
      });
      if (publish) {
        setMessage('Regra salva para os próximos documentos.');
        setPreview(undefined);
        onSaved();
      } else {
        setPreview(result.value);
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Não foi possível aplicar a regra.');
    } finally {
      setBusy(false);
    }
  }

  return <details className="entry-options">
    <summary>Ensinar onde encontrar este campo</summary>
    <fieldset disabled={busy}>
      <label className="control"><span>Texto que identifica este layout</span>
        <input placeholder="Ex.: Nome da seguradora" value={marker} onChange={e => { setMarker(e.target.value); setPreview(undefined); }}/>
      </label>
      <label className="control"><span>Rótulo impresso ao lado do valor</span>
        <input placeholder="Ex.: Placa do veículo" value={anchor} onChange={e => { setAnchor(e.target.value); setPreview(undefined); }}/>
      </label>
      <label className="control"><span>Outro arquivo do mesmo layout</span>
        <select value={testId} onChange={e => { setTestId(e.target.value); setExpected(''); setPreview(undefined); }}>
          <option value="">Escolha um arquivo de teste</option>
          {testFiles.map(item => <option key={item.id} value={item.id}>{item.fileName}</option>)}
        </select>
      </label>
      <button type="button" className="secondary" disabled={!testId || marker.trim().length < 2 || anchor.trim().length < 2}
        onClick={() => void submit(false)}>Testar regra</button>
      {preview !== undefined && <p>Valor encontrado: <strong>{preview || 'Nenhum valor encontrado pela regra'}</strong></p>}
      {!!preview && <>
        <label className="control"><span>Valor correto no arquivo de teste</span>
          <input value={expected} onChange={e => setExpected(e.target.value)}/>
        </label>
        <button type="button" className="primary" disabled={preview !== expected.trim()}
          onClick={() => void submit(true)}>Publicar regra</button>
      </>}
      {message && <p role="status">{message}</p>}
    </fieldset>
  </details>;
}
