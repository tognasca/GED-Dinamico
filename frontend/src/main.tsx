import { lazy, Suspense } from 'react';
import React, { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Archive, Boxes, CheckCircle2, ChevronRight, CloudUpload, FileSearch, FileText, FolderOpen, Layers, LoaderCircle, Plus, ScanLine, Settings2, Sparkles, XCircle } from 'lucide-react';
import { api, API_URL, DEMO_TENANT, type Batch, type DocumentType, type Field, type FieldResult, type GedDocument, type Suggestion } from './api';
import './style.css';
import LayoutRuleEditor from './LayoutRuleEditor';

const PdfPicker = lazy(() => import('./PdfPicker'));

type View = 'review' | 'capture' | 'models' | 'documents' | 'batches' | 'physical';
const defaultField = (): Field => ({ key: '', label: '', dataType: 'text', required: false, anchor: '' });

function normalizeFieldKey(value: string): string {
  const normalized = value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/[^a-zA-Z0-9_]+/g, '_').replace(/^_+|_+$/g, '').toLowerCase();
  const key = /^[a-z]/.test(normalized) ? normalized : `campo_${normalized || 'novo'}`;
  return key.slice(0, 64);
}

function prepareFields(fields: Field[]): Field[] {
  const seen = new Set<string>();
  return fields.map((field, index) => {
    const base = normalizeFieldKey(field.key || field.label || `campo_${index + 1}`);
    let key = base;
    let suffix = 2;
    while (seen.has(key)) {
      const ending = `_${suffix++}`;
      key = base.slice(0, 64 - ending.length) + ending;
    }
    seen.add(key);
    return { ...field, key, label: field.label.trim() || key, layouts: field.layouts?.map(rule => ({ marker: rule.marker.trim(), anchor: rule.anchor.trim() })).filter(rule => rule.marker && rule.anchor) };
  });
}

function sourceExcerpt(text: string, value: string) {
  const needle = value.trim();
  if (needle.length < 3) return null;
  const start = text.indexOf(needle);
  if (start < 0 || text.indexOf(needle, start + 1) >= 0) return null;
  const end = start + needle.length;
  return { before: text.slice(Math.max(0, start - 48), start), value: text.slice(start, end), after: text.slice(end, end + 48) };
}

function reviewIssue(field: Field, value: string, source?: string) {
  if (source === 'ambiguous-layout') return 'Layout ambíguo';
  if (!value.trim()) return field.required ? 'Campo obrigatório vazio' : 'Campo vazio';
  if (field.dataType === 'date' && !/^(\d{1,2}\/\d{1,2}\/\d{4}|\d{4}-\d{2}-\d{2})$/.test(value.trim()))
    return 'Use uma data no formato DD/MM/AAAA';
  if (field.dataType === 'currency' && !/^(R\$\s*)?-?[\d.]+(,\d+)?$/.test(value.trim()))
    return 'Use um valor como 1.234,56';
  return '';
}

function App() {
  const [view, setView] = useState<View>('capture');
  const [types, setTypes] = useState<DocumentType[]>([]);
  const [documents, setDocuments] = useState<GedDocument[]>([]);
  const [batches, setBatches] = useState<Batch[]>([]);
  const [selected, setSelected] = useState<GedDocument | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [modelStep, setModelStep] = useState(1);
  const [modelName, setModelName] = useState('');
  const [keywords, setKeywords] = useState('');
  const [fields, setFields] = useState<Field[]>([]);
  const [chosenTemplate, setChosenTemplate] = useState('');
  const [corrections, setCorrections] = useState<Record<string, string>>({});
  const [testId, setTestId] = useState('');
  const [preview, setPreview] = useState<{ signature: string; values: Record<string, FieldResult> }>();
  const [onlyPending, setOnlyPending] = useState(false);
  const [pickField, setPickField] = useState<Field>();
  const [reviewTab, setReviewTab] = useState<'fields' | 'file'>('fields');
  const [fileUrl, setFileUrl] = useState('');
  const [filterType, setFilterType] = useState('');
  const [filterField, setFilterField] = useState('');
  const [filterValue, setFilterValue] = useState('');
  const [libraryPage, setLibraryPage] = useState(1);
  const [libraryQuery, setLibraryQuery] = useState('');
  const [hasMore, setHasMore] = useState(false);
  const [results, setResults] = useState<GedDocument[] | null>(null);
  const signature = JSON.stringify([testId, prepareFields(fields)]);
  useEffect(() => {
    setFileUrl('');
    setReviewTab('fields');
    setPickField(undefined);
    setOnlyPending(false);
    if (!selected || (selected.contentType !== 'application/pdf' && !selected.contentType.startsWith('image/'))) return;
    const controller = new AbortController();
    let url: string | undefined;
    void fetch(`${API_URL}/api/documents/${selected.id}/file`, {
      headers: { 'X-Tenant-Id': DEMO_TENANT }, signal: controller.signal,
    }).then(response => {
      if (!response.ok) throw new Error('Não foi possível abrir o arquivo.');
      return response.blob();
    }).then(blob => {
      if (!controller.signal.aborted) {
        url = URL.createObjectURL(blob);
        setFileUrl(url);
      }
    }).catch(error => { if (!controller.signal.aborted) setError(String(error)); });
    return () => { controller.abort(); if (url) URL.revokeObjectURL(url); };
  }, [selected?.id, selected?.contentType]);
  const [search, setSearch] = useState('');
  const [batchName, setBatchName] = useState('');
  const [batchId, setBatchId] = useState('');
  const [locations, setLocations] = useState<{ id: string; name: string; kind: string; parentId?: string }[]>([]);
  const [boxes, setBoxes] = useState<{ id: string; code: string; locationId: string }[]>([]);
  const [locationName, setLocationName] = useState('');
  const [locationKind, setLocationKind] = useState('Prateleira');
  const [locationParent, setLocationParent] = useState('');
  const [boxCode, setBoxCode] = useState('');
  const [boxLocation, setBoxLocation] = useState('');
  const [assignBoxId, setAssignBoxId] = useState('');
  const [route, setRoute] = useState('');

  async function reload() {
    try {
      const [t, d, b, l, bx] = await Promise.all([
        api<DocumentType[]>('/types'), api<GedDocument[]>('/documents'), api<Batch[]>('/batches'),
        api<typeof locations>('/locations'), api<typeof boxes>('/boxes'),
      ]);
      setTypes(t); setDocuments(d); setBatches(b); setLocations(l); setBoxes(bx);
    } catch (e) { setError(String(e)); }
  }
  useEffect(() => { void reload(); }, []);
  const current = useMemo(() => types.find(t => t.id === selected?.documentTypeId), [types, selected]);
  const suggestions = Array.isArray(selected?.suggestions) ? selected.suggestions as Suggestion[] : [];

  async function perform(task: () => Promise<void>) {
    setBusy(true); setError(''); setNotice('');
    try { await task(); await reload(); } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  async function upload() {
    if (!file) return;
    await perform(async () => {
      const form = new FormData(); form.append('file', file);
      const suffix = batchId ? `?batchId=${batchId}` : '';
      const doc = await api<GedDocument>(`/documents/upload${suffix}`, { method: 'POST', body: form });
      setSelected(doc); setChosenTemplate(doc.documentTypeId ?? ''); setCorrections({}); setTestId(doc.id); setPreview(undefined); setModelStep(1); setFile(null); setView(doc.documentTypeId ? 'review' : 'capture');
      setFields(prepareFields((Array.isArray(doc.suggestions) ? doc.suggestions as Suggestion[] : []).map(s => ({ key: s.key, label: s.label, dataType: s.dataType, required: false, anchor: s.label }))));
      setNotice(doc.ocrStatus === 'Completed' ? doc.documentTypeId ? 'Confira os campos preenchidos.' : 'Escolha o tipo do documento para continuar.' : 'Documento salvo, mas o OCR precisa de atenção.');
    });
  }
  async function testModel() {
    await perform(async () => {
      const values = await api<Record<string, FieldResult>>('/types/preview', {
        method: 'POST', body: JSON.stringify({ documentId: testId, fields: prepareFields(fields) }),
      });
      setPreview({ signature, values }); setModelStep(3);
    });
  }
  async function searchLibrary(page = 1, reuseFilters = false) {
    await perform(async () => {
      const query = !reuseFilters ? new URLSearchParams({ q: search.trim() }) : new URLSearchParams(libraryQuery || undefined);
      if (!reuseFilters && filterType) query.set('typeId', filterType);
      if (!reuseFilters && filterField && filterValue.trim()) {
        query.set('field', filterField);
        query.set('value', filterValue.trim());
      }
      setLibraryQuery(query.toString());
      query.set('page', String(page));
      query.set('pageSize', '25');
      const result = await api<{ items: GedDocument[]; hasMore: boolean }>(`/documents?${query}`);
      setResults(result.items);
      setLibraryPage(page);
      setHasMore(result.hasMore);
    });
  }
  async function createType() {
    await perform(async () => {
      const result = await api<{ id: string }>('/types', { method: 'POST', body: JSON.stringify({ name: modelName, keywords: keywords.split(',').map(x => x.trim()).filter(Boolean), fields: prepareFields(fields) }) });
      setChosenTemplate(result.id); setModelName(''); setModelStep(1); setNotice('Tipo salvo. Clique em Continuar para conferência.');
    });
  }
  async function applyTemplate(id: string) {
    if (!selected || !id) return;
    await perform(async () => {
      const doc = await api<GedDocument>(`/documents/${selected.id}/apply-template`, { method: 'POST', body: JSON.stringify({ documentTypeId: id }) });
      setSelected(doc); setCorrections({}); setView('review'); setNotice('Extração realizada. Confirme os campos para indexar.');
    });
  }
  async function confirmDocument(advance = false) {
    if (!selected) return;
    await perform(async () => {
      const doc = await api<GedDocument>(`/documents/${selected.id}/confirm`, { method: 'POST', body: JSON.stringify({ values: corrections }) });
      setResults(null);
      const next = advance ? await api<GedDocument[]>('/documents').then(items =>
        items.find(item => item.id !== doc.id && item.ocrStatus === 'Completed' &&
          (item.status === 'NeedsReview' || item.status === 'AwaitingTemplate'))).catch(() => undefined) : undefined;
      if (next) openDoc(next);
      else { setSelected(doc); setView('documents'); }
      setNotice('Documento salvo na biblioteca.');
    });
  }
  async function retryDocument(id: string) {
    await perform(async () => {
      const doc = await api<GedDocument>(`/documents/${id}/retry`, { method: 'POST' });
      if (doc.ocrStatus === 'Completed') openDoc(doc);
      else setError('Ainda não foi possível ler o arquivo. Se estiver ilegível, importe uma cópia mais nítida.');
    });
  }
  function openDoc(doc: GedDocument) {
    setSelected(doc); setTestId(doc.id); setPreview(undefined); setModelStep(1); setView(doc.documentTypeId ? 'review' : 'capture'); setCorrections({}); setChosenTemplate(doc.documentTypeId || '');
    setFields(Array.isArray(doc.suggestions) ? (doc.suggestions as Suggestion[]).map(s => ({ key: s.key, label: s.label, dataType: s.dataType, required: false, anchor: s.label })) : []);
  }
  const menu: { id: View; label: string }[] = [
    { id: 'capture', label: 'Caixa de entrada' },
    { id: 'review', label: 'Conferência' },
    { id: 'documents', label: 'Biblioteca' },
    { id: 'models', label: 'Modelos' },
    { id: 'batches', label: 'Lotes' },
    { id: 'physical', label: 'Arquivo físico' },
  ];
  const pendingDocuments = documents.filter(doc => doc.status === 'NeedsReview' || doc.status === 'AwaitingTemplate' || doc.ocrStatus === 'Pending' || doc.ocrStatus === 'Failed');

  return <div className={view === 'review' ? 'app ged-shell review-page' : 'app ged-shell'}>
    <header className="review-nav">
      <strong>GED <span>Dinâmico</span></strong>
      <nav aria-label="Navegação do GED">
        {menu.map(item => <button key={item.id} aria-current={view === item.id ? 'page' : undefined}
          onClick={() => { if (item.id === 'capture' || (item.id === 'review' && view !== 'review')) { setSelected(null); setCorrections({}); } setView(item.id); }}>{item.label}</button>)}
      </nav>
    </header>
    <main className="main">
      {error && <div className="alert error"><XCircle size={16}/>{error}<button onClick={() => setError('')}>×</button></div>}
      {notice && <div className="alert success"><CheckCircle2 size={16}/>{notice}<button onClick={() => setNotice('')}>×</button></div>}
      {view === 'review' && !current && <section className="panel review-queue">
        <h1>Documentos para conferir</h1>
        {pendingDocuments.length ? <div className="items">{pendingDocuments.map(doc =>
          <button className="item" key={doc.id} disabled={busy || doc.ocrStatus === 'Pending'} onClick={() => doc.ocrStatus === 'Failed' ? void retryDocument(doc.id) : openDoc(doc)}>
            <FileText size={20}/><span>{doc.fileName}</span>
            <small>{doc.ocrStatus === 'Failed' ? 'Tentar novamente' : doc.ocrStatus === 'Pending' ? 'Aguardando leitura' : doc.documentTypeId ? 'Conferir campos' : 'Escolher tipo'}</small><ChevronRight size={16}/>
          </button>)}</div> : <p>Nenhum documento aguardando conferência.</p>}
      </section>}
      {view === 'review' && selected && current && <section className="review">
              <header><h1>Conferir documento</h1><p>{selected.fileName}</p></header>
              {fileUrl && <div className="review-tabs" role="group" aria-label="Visualização da conferência">
                <button type="button" aria-pressed={reviewTab === 'fields'} onClick={()=>setReviewTab('fields')}>Campos</button>
                <button type="button" aria-pressed={reviewTab === 'file'} onClick={()=>setReviewTab('file')}>Arquivo</button>
              </div>}
              <div className={`review-grid review-tab-${reviewTab}`}>
                {fileUrl && (selected.contentType === 'application/pdf'
                  ? pickField ? <Suspense fallback={<p className="review-file">Abrindo seleção do PDF…</p>}>
                    <PdfPicker key={selected.id + pickField.key} url={fileUrl} documentId={selected.id} field={pickField.label}
                      onCancel={() => { setPickField(undefined); setReviewTab('fields'); }}
                      onPick={value => {
                        setCorrections(previous => ({ ...previous, [pickField.key]: value }));
                        setPickField(undefined);
                        setReviewTab('fields');
                        setNotice(`${pickField.label} preenchido a partir do PDF.`);
                      }}/>
                  </Suspense> : <iframe className="review-file" title="PDF para conferência" src={fileUrl}/>
                  : <div className="review-file review-image">
                      {selected.contentType !== 'image/tiff' && <img src={fileUrl} alt={selected.fileName}/>}
                      <a href={fileUrl} download={selected.fileName}>{selected.contentType === 'image/tiff' ? 'Baixar imagem TIFF para conferir' : 'Baixar imagem original'}</a>
                    </div>)}
                <div className="review-form">
                  <h2>Campos do documento</h2>
                  <div className="review-model">{current.name}</div>
                  <label className="checkbox"><input type="checkbox" checked={onlyPending} onChange={e=>setOnlyPending(e.target.checked)}/> Mostrar só pendências</label>
                  {current.fields.map(field => {
                    const value = corrections[field.key] ?? selected.values?.[field.key]?.value ?? '';
                    const issue = reviewIssue(field, value, corrections[field.key] === undefined ? selected.values?.[field.key]?.source : undefined);
                    if (onlyPending && !issue) return null;
                    const evidence = corrections[field.key] === undefined ? sourceExcerpt(selected.ocrText, value) : null;
                    return <div className="control" key={field.key}><label className="control pdf-field-label">
                      <span>{field.label}{field.required ? ' *' : ''}</span>
                      <input value={value} aria-invalid={Boolean(issue && (field.required || value.trim()))} onChange={e=>setCorrections(previous=>({...previous,[field.key]:e.target.value}))}/>
                      {issue && <small className="field-warning">{issue}</small>}
                      </label>
                      {fileUrl && selected.contentType === 'application/pdf' && <button type="button" className="secondary"
                        aria-pressed={pickField?.key === field.key}
                        onClick={() => { setPickField(field); setReviewTab('file'); }}>Selecionar no PDF</button>}
                      {selected.ocrStatus === 'Completed' &&
                        <LayoutRuleEditor document={selected} documents={documents} field={field}
                          version={current.version} sourceUrl={fileUrl} currentValue={value}
                          onSaved={() => { setNotice('Regra salva para os próximos documentos.'); void reload(); }}/>}
                      {evidence && <details><summary>Ver trecho reconhecido</summary>
                        <small>{evidence.before}<mark>{evidence.value}</mark>{evidence.after}</small>
                      </details>}
                    </div>;
                  })}
                </div>
              </div>
              <footer className="review-actions"><button className="secondary" onClick={()=>setView('capture')}>Alterar modelo</button><button className="primary" disabled={busy} onClick={()=>void confirmDocument()}><CheckCircle2 size={17}/> Salvar na biblioteca</button>{pendingDocuments.some(doc => doc.id !== selected.id && doc.ocrStatus === 'Completed') && <button className="primary" disabled={busy} onClick={()=>void confirmDocument(true)}>Salvar e abrir o próximo</button>}</footer>
            </section>}
      {view === 'capture' && <div className="entry">
        <header className="entry-heading"><h1>{selected ? 'Preparar documento' : 'Caixa de entrada'}</h1>
          <p>{selected ? selected.fileName : 'Selecione um arquivo. O GED vai ler o documento e preparar os campos para conferência.'}</p>
        </header>
        <div className="entry-flow">
        {!selected && <section className="panel entry-upload">
          <label className="dropzone"><CloudUpload size={30}/><strong>{file ? file.name : 'Escolha um documento'}</strong><span>PDF, PNG, JPG, TIFF, WEBP ou TXT (teste) · Até 25 MB</span><input type="file" accept=".pdf,.png,.jpg,.jpeg,.tif,.tiff,.webp,.txt" onChange={e=>setFile(e.target.files?.[0] ?? null)}/></label>
          <label className="field-label">Lote (opcional)</label><select value={batchId} onChange={e=>setBatchId(e.target.value)}><option value="">Sem lote</option>{batches.filter(x=>x.status==='Open').map(b=><option key={b.id} value={b.id}>{b.name}</option>)}</select>
          <button className="primary full" disabled={!file || busy} onClick={() => void upload()}>{busy ? <LoaderCircle size={17} className="spin"/> : <ScanLine size={17}/>} Processar documento</button>
        </section>}
        {selected?.ocrStatus === 'Failed' && <section className="panel">
          <h2>Não conseguimos ler este arquivo</h2>
          <div className="inline"><button className="primary" disabled={busy} onClick={()=>void retryDocument(selected.id)}>Tentar novamente</button>
          <button className="secondary" onClick={()=>setSelected(null)}>Voltar à caixa de entrada</button></div>
        </section>}
        {selected && selected.ocrStatus !== 'Failed' && <section className="panel"><div className="panel-title"><div><h2>Modelo do documento</h2></div><Settings2 size={21}/></div>
            {selected.documentTypeId && <span className="model-tag"><CheckCircle2 size={15}/> Modelo aplicado: {types.find(t=>t.id===selected.documentTypeId)?.name ?? 'Existente'}</span>}
            <label className="field-label">Tipo de documento</label><div className="inline"><select value={chosenTemplate} onChange={e=>setChosenTemplate(e.target.value)}><option value="">Selecione o tipo</option>{types.map(t=><option key={t.id} value={t.id}>{t.name}</option>)}</select><button className="secondary" disabled={!chosenTemplate || busy || selected.ocrStatus!=='Completed'} onClick={()=>void applyTemplate(chosenTemplate)}>Continuar para conferência</button></div>
            {current && <button className="secondary full" onClick={()=>setView('review')}>Conferir documento</button>}
            <details className="entry-options"><summary>Criar tipo a partir deste exemplo</summary>
            <p className="wizard-step">Passo {modelStep} de 3</p>
            {modelStep === 1 && <>
            <label className="control"><span>Nome do modelo</span><input placeholder="Ex.: Cheque bancário" value={modelName} onChange={e=>setModelName(e.target.value)}/></label>
            <button className="primary" disabled={!modelName.trim()} onClick={()=>setModelStep(2)}>Continuar</button></>}
            {modelStep === 2 && <>
            <h3>Quais informações você quer guardar?</h3>
            <p>Edite as sugestões, remova o que sobra ou adicione um campo.</p>
            <div className="suggestions"><div className="subheading">Informações encontradas <span>{suggestions.length}</span></div>{suggestions.length===0 && <p className="muted">Adicione as informações que deseja guardar, como número do contrato ou data.</p>}
              {fields.map((field,i)=><div className="field-row" key={i}><input value={field.label} aria-label="Nome da informação" placeholder="Ex.: Número da apólice" onChange={e=>setFields(f=>f.map((x,j)=>j===i?{...x,label:e.target.value}:x))}/><select aria-label="Tipo do campo" value={field.dataType} onChange={e=>setFields(items=>items.map((x,j)=>j===i?{...x,dataType:e.target.value}:x))}><option value="text">Texto</option><option value="date">Data</option><option value="currency">Valor</option></select><label className="checkbox"><input type="checkbox" checked={field.required} onChange={e=>setFields(f=>f.map((x,j)=>j===i?{...x,required:e.target.checked}:x))}/>Obrigatório</label><button className="icon-button" title="Remover campo" onClick={()=>setFields(s=>s.filter((_,j)=>j!==i))}><XCircle size={17}/></button><details><summary>Opções para documentos com formatos diferentes</summary>{field.layouts?.map((layout,k)=><div className="inline" key={k}>{(['marker','anchor'] as const).map(key=><input key={key} aria-label={key==='marker'?'Identificador do layout':'Rótulo no PDF'} placeholder={key==='marker'?'Ex.: Porto Seguro':'Ex.: Nº da apólice'} value={layout[key]} onChange={e=>setFields(items=>items.map((x,j)=>j===i?{...x,layouts:x.layouts?.map((rule,n)=>n===k?{...rule,[key]:e.target.value}:rule)}:x))}/>)}<button onClick={()=>setFields(items=>items.map((x,j)=>j===i?{...x,layouts:x.layouts?.filter((_,n)=>n!==k)}:x))}>Remover</button></div>)}<button onClick={()=>setFields(items=>items.map((x,j)=>j===i?{...x,layouts:[...(x.layouts??[]),{marker:'',anchor:''}]}:x))}>Adicionar layout</button></details></div>)}
              <button className="text-button" onClick={()=>setFields(s=>[...s,defaultField()])}><Plus size={16}/> Adicionar campo</button></div>
            <details className="entry-options"><summary>Opções de reconhecimento</summary>
              <label className="control"><span>Palavras que ajudam a reconhecer este tipo</span><input placeholder="Ex.: contrato, locação" value={keywords} onChange={e=>setKeywords(e.target.value)}/></label>
            <label className="control"><span>Testar com outro arquivo</span><select value={testId} onChange={e=>setTestId(e.target.value)}><option value="">Selecione</option>{documents.filter(d=>d.ocrStatus==='Completed').map(d=><option key={d.id} value={d.id}>{d.fileName}</option>)}</select></label>
            </details>
            <div className="inline"><button className="secondary" onClick={()=>setModelStep(1)}>Voltar</button>
            <button className="primary" disabled={busy || !testId || !fields.length || fields.some(f=>!f.label.trim())} onClick={()=>void testModel()}>Conferir resultado</button></div></>}
            {modelStep === 3 && <>
            <h3>Confira o que o GED encontrou</h3>
            <p>Se faltar algum valor, você pode voltar e ajustar os campos ou salvar o tipo e preencher na conferência.</p>
            {preview?.signature===signature && <dl>{prepareFields(fields).map(f=><React.Fragment key={f.key}><dt>{f.label}</dt><dd>{preview.values[f.key]?.value || 'Campo vazio'}</dd></React.Fragment>)}</dl>}
            <button className="secondary" onClick={()=>setModelStep(2)}>Voltar e ajustar</button>
            <button className="primary full" disabled={busy || preview?.signature!==signature || !modelName.trim() || fields.length===0} onClick={()=>void createType()}><Sparkles size={17}/> Salvar tipo de documento</button>
            </>}
            </details>
            <details className="entry-options"><summary>Ver arquivo e texto reconhecido</summary>
            <div className="preview"><FileText size={27}/><div><strong>{selected.fileName}</strong><span>{selected.ocrStatus} · {(selected.fileSize/1024).toFixed(1)} KB</span></div><a href={`${API_URL}/api/documents/${selected.id}/file`} onClick={e=>{e.preventDefault();void fetch(`${API_URL}/api/documents/${selected.id}/file`, {headers:{'X-Tenant-Id':DEMO_TENANT}}).then(r=>{if(!r.ok)throw new Error('Falha ao abrir arquivo');return r.blob()}).then(b=>{const url=URL.createObjectURL(b);window.open(url,'_blank','noopener,noreferrer');setTimeout(()=>URL.revokeObjectURL(url),60000)}).catch(err=>setError(String(err)))}}>Abrir</a></div>
            <label className="field-label">Texto reconhecido</label><pre className="ocr-text">{selected.ocrText || 'Nenhum texto reconhecido.'}</pre>
            </details>
            <button className="secondary" onClick={()=>{setSelected(null);setCorrections({});setNotice('');setError('');}}>Importar outro arquivo</button>
        </section>}
        </div>
        {!selected && <section className="panel entry-pending">
          <h2>Documentos pendentes</h2>
          {pendingDocuments.length ? <div className="items">{pendingDocuments.map(doc =>
            <button className="item" key={doc.id} disabled={busy || doc.ocrStatus === 'Pending'}
              onClick={() => doc.ocrStatus === 'Failed' ? void retryDocument(doc.id) : openDoc(doc)}>
              <FileText size={20}/><span>{doc.fileName}</span>
              <small>{doc.ocrStatus === 'Failed' ? 'Tentar novamente' : doc.ocrStatus === 'Pending' ? 'Aguardando leitura' :
                doc.documentTypeId ? 'Conferir campos' : 'Escolher tipo'}</small>
            </button>)}</div> : <p>Nenhum documento pendente.</p>}
        </section>}
      </div>}
      {view === 'documents' && <section className="panel"><div className="panel-title"><div><h2>Biblioteca documental</h2><p>Busca por tipo, campo indexado ou texto OCR</p></div><FileSearch size={21}/></div><input placeholder="Pesquisar documentos..." value={search} onChange={e=>setSearch(e.target.value)}/><div className="inline"><select aria-label="Tipo de documento" value={filterType} onChange={e=>{setFilterType(e.target.value);setFilterField('');setFilterValue('')}}><option value="">Todos os tipos</option>{types.map(t=><option key={t.id} value={t.id}>{t.name}</option>)}</select>{filterType && <select aria-label="Campo indexado" value={filterField} onChange={e=>setFilterField(e.target.value)}><option value="">Qualquer campo</option>{types.find(t=>t.id===filterType)?.fields.map(f=><option key={f.key} value={f.key}>{f.label}</option>)}</select>}{filterField && <input aria-label="Valor do campo" value={filterValue} onChange={e=>setFilterValue(e.target.value)}/>}<button className="secondary" disabled={busy} onClick={()=>void searchLibrary()}>Buscar</button></div><div className="items">{(results ?? documents).map(d=><button className="item" key={d.id} onClick={()=>openDoc(d)}><FileText size={20}/><span><strong>{d.fileName}</strong><small>{new Date(d.createdAt).toLocaleString('pt-BR')}</small></span><span className="chip">{d.status}</span><ChevronRight size={15}/></button>)}</div>{results && <div className="inline library-pages">
        <button className="secondary" disabled={busy || libraryPage === 1} onClick={()=>void searchLibrary(libraryPage - 1, true)}>Anterior</button>
        <span>Página {libraryPage}</span>
        <button className="secondary" disabled={busy || !hasMore} onClick={()=>void searchLibrary(libraryPage + 1, true)}>Próxima</button>
      </div>}</section>}
      {view === 'models' && <section className="panel"><div className="panel-title"><div><h2>Catálogo de modelos</h2><p>Estruturas criadas sem código</p></div><Layers size={22}/></div><div className="items">{types.length===0&&<p className="muted">Nenhum modelo criado. Vá à Central de digitalização para ensinar o primeiro.</p>}{types.map(t=><div className="item" key={t.id}><Layers size={20}/><span><strong>{t.name}</strong><small>{t.fields.map(f=>f.label).join(' · ')}</small></span><span className="chip">v{t.version}</span></div>)}</div></section>}
      {view === 'batches' && <section className="panel"><div className="panel-title"><div><h2>Lotes de digitalização</h2><p>Organize o processamento em grupos</p></div><FolderOpen size={21}/></div><div className="inline"><input placeholder="Nome do novo lote" value={batchName} onChange={e=>setBatchName(e.target.value)}/><button className="primary" disabled={!batchName.trim()||busy} onClick={()=>void perform(async()=>{await api('/batches',{method:'POST',body:JSON.stringify({name:batchName})});setBatchName('')})}><Plus size={16}/> Criar lote</button></div><div className="items">{batches.map(b=><div className="item" key={b.id}><FolderOpen size={20}/><span><strong>{b.name}</strong><small>{documents.filter(d=>d.batchId===b.id).length} documento(s)</small></span><span className="chip">{b.status}</span>{b.status==='Open'&&<button className="secondary" disabled={busy} onClick={()=>void perform(async()=>{await api(`/batches/${b.id}/close`,{method:'POST'});setNotice('Lote finalizado')})}>Finalizar</button>}</div>)}</div></section>}
      {view === 'physical' && <div className="workspace"><section className="panel"><div className="panel-title"><div><h2>Localizações dinâmicas</h2><p>Depósito → Fileira → Estante → Prateleira</p></div><Archive size={21}/></div><label className="control"><span>Nome</span><input value={locationName} onChange={e=>setLocationName(e.target.value)} placeholder="Ex.: Corredor 03"/></label><label className="control"><span>Tipo</span><input value={locationKind} onChange={e=>setLocationKind(e.target.value)}/></label><label className="control"><span>Dentro de</span><select value={locationParent} onChange={e=>setLocationParent(e.target.value)}><option value="">Raiz</option>{locations.map(l=><option key={l.id} value={l.id}>{l.name}</option>)}</select></label><button className="primary full" disabled={!locationName.trim()||busy} onClick={()=>void perform(async()=>{await api('/locations',{method:'POST',body:JSON.stringify({name:locationName,kind:locationKind,parentId:locationParent||null})});setLocationName('')})}>Adicionar localização</button><div className="items">{locations.map(l=><div className="item" key={l.id}><Archive size={17}/><span><strong>{l.name}</strong><small>{l.kind} · {locations.find(p=>p.id===l.parentId)?.name||'Raiz'}</small></span></div>)}</div></section><section className="panel"><div className="panel-title"><div><h2>Caixas e rastreabilidade</h2><p>Vincule caixas a locais e documentos</p></div><Boxes size={21}/></div><label className="control"><span>Código da caixa</span><input value={boxCode} onChange={e=>setBoxCode(e.target.value)} placeholder="CX-00125"/></label><label className="control"><span>Localização</span><select value={boxLocation} onChange={e=>setBoxLocation(e.target.value)}><option value="">Selecione...</option>{locations.map(l=><option key={l.id} value={l.id}>{l.name}</option>)}</select></label><button className="primary full" disabled={!boxCode||!boxLocation||busy} onClick={()=>void perform(async()=>{await api('/boxes',{method:'POST',body:JSON.stringify({code:boxCode,locationId:boxLocation})});setBoxCode('')})}>Cadastrar caixa</button><div className="items">{boxes.map(b=><button className="item" key={b.id} onClick={()=>void perform(async()=>{const result=await api<{route:{name:string}[];code:string}>(`/boxes/${b.id}/route`);setRoute(result.route.map(r=>r.name).join(' → ')+' → '+result.code)})}><Boxes size={17}/><span><strong>{b.code}</strong><small>Ver rota física</small></span><ChevronRight size={15}/></button>)}</div>{route&&<div className="route">{route}</div>}{selected&&<><div className="divider"/><label className="control"><span>Vincular caixa a {selected.fileName}</span><select value={assignBoxId} onChange={e=>setAssignBoxId(e.target.value)}><option value="">Selecione...</option>{boxes.map(b=><option key={b.id} value={b.id}>{b.code}</option>)}</select></label><button className="secondary full" disabled={!assignBoxId||busy} onClick={()=>void perform(async()=>{const d=await api<GedDocument>(`/documents/${selected.id}/box`,{method:'POST',body:JSON.stringify({boxId:assignBoxId})});setSelected(d);setNotice('Caixa vinculada')})}>Associar documento</button></>}</section></div>}
      <footer>GED Dinâmico · MVP de desenvolvimento · Não expor na Internet sem autenticação e isolamento de tenant</footer>
    </main>
  </div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
