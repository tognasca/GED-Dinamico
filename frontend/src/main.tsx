import React, { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Archive, Boxes, CheckCircle2, ChevronRight, CloudUpload, Database, FileSearch, FileText, FolderOpen, Layers, LoaderCircle, Plus, ScanLine, Settings2, Sparkles, XCircle } from 'lucide-react';
import { api, API_URL, DEMO_TENANT, type Batch, type DocumentType, type Field, type GedDocument, type Suggestion } from './api';
import './style.css';

type View = 'capture' | 'models' | 'documents' | 'batches' | 'physical';
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
    return { ...field, key, label: field.label.trim() || key };
  });
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
  const [modelName, setModelName] = useState('');
  const [keywords, setKeywords] = useState('');
  const [fields, setFields] = useState<Field[]>([]);
  const [chosenTemplate, setChosenTemplate] = useState('');
  const [corrections, setCorrections] = useState<Record<string, string>>({});
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
      setSelected(doc); setFile(null); setView('capture');
      setFields(prepareFields((Array.isArray(doc.suggestions) ? doc.suggestions as Suggestion[] : []).map(s => ({ key: s.key, label: s.label, dataType: s.dataType, required: false, anchor: s.label }))));
      setNotice(doc.ocrStatus === 'Completed' ? 'OCR concluído. Confira os campos e configure seu modelo.' : 'Documento salvo, mas o OCR precisa de atenção.');
    });
  }
  async function createType() {
    await perform(async () => {
      const result = await api<{ id: string }>('/types', { method: 'POST', body: JSON.stringify({ name: modelName, keywords: keywords.split(',').map(x => x.trim()).filter(Boolean), fields: prepareFields(fields) }) });
      setChosenTemplate(result.id); setModelName(''); setNotice('Modelo criado. Aplique-o ao documento para extrair os campos.');
    });
  }
  async function applyTemplate(id: string) {
    if (!selected || !id) return;
    await perform(async () => {
      const doc = await api<GedDocument>(`/documents/${selected.id}/apply-template`, { method: 'POST', body: JSON.stringify({ documentTypeId: id }) });
      setSelected(doc); setCorrections({}); setNotice('Extração realizada. Confirme os campos para indexar.');
    });
  }
  async function confirmDocument() {
    if (!selected) return;
    await perform(async () => {
      const doc = await api<GedDocument>(`/documents/${selected.id}/confirm`, { method: 'POST', body: JSON.stringify({ values: corrections }) });
      setSelected(doc); setNotice('Documento indexado com sucesso.');
    });
  }
  function openDoc(doc: GedDocument) {
    setSelected(doc); setView('capture'); setCorrections({}); setChosenTemplate(doc.documentTypeId || '');
    setFields(Array.isArray(doc.suggestions) ? (doc.suggestions as Suggestion[]).map(s => ({ key: s.key, label: s.label, dataType: s.dataType, required: false, anchor: s.label })) : []);
  }
  const menu: { id: View; label: string; icon: React.ReactNode }[] = [
    { id: 'capture', label: 'Central de digitalização', icon: <ScanLine size={18} /> },
    { id: 'documents', label: 'Documentos', icon: <FileSearch size={18} /> },
    { id: 'models', label: 'Modelos dinâmicos', icon: <Layers size={18} /> },
    { id: 'batches', label: 'Lotes', icon: <FolderOpen size={18} /> },
    { id: 'physical', label: 'Arquivo físico', icon: <Boxes size={18} /> },
  ];

  return <div className="app">
    <aside className="sidebar"><div className="brand"><div className="brand-icon"><Database size={22}/></div><div><b>GED Dinâmico</b><small>Document intelligence</small></div></div>
      <div className="sidebar-heading">WORKSPACE</div>
      <nav>{menu.map(item => <button key={item.id} className={view === item.id ? 'nav active' : 'nav'} onClick={() => setView(item.id)}>{item.icon}<span>{item.label}</span>{view === item.id && <ChevronRight size={15}/>}</button>)}</nav>
      <div className="side-bottom"><Sparkles size={18}/><span>Primeiro modelo, depois automação.</span></div>
    </aside>
    <main className="main">
      <header className="top"><div><span className="crumb">GED / Painel operacional</span><h1>{menu.find(x => x.id === view)?.label}</h1></div><span className="environment">Ambiente de desenvolvimento</span></header>
      {error && <div className="alert error"><XCircle size={16}/>{error}<button onClick={() => setError('')}>×</button></div>}
      {notice && <div className="alert success"><CheckCircle2 size={16}/>{notice}<button onClick={() => setNotice('')}>×</button></div>}
      <div className="stats"><div className="stat"><span>Documentos</span><strong>{documents.length}</strong><small>Capturados</small></div><div className="stat"><span>Modelos</span><strong>{types.length}</strong><small>Tipos configurados</small></div><div className="stat"><span>Para revisão</span><strong>{documents.filter(d => d.status === 'NeedsReview' || d.status === 'AwaitingTemplate').length}</strong><small>Necessitam conferência</small></div><div className="stat"><span>Indexados</span><strong>{documents.filter(d => d.status === 'Indexed').length}</strong><small>Disponíveis para consulta</small></div></div>
      {view === 'capture' && <div className="workspace">
        <section className="panel"><div className="panel-title"><div><h2>01. Importar documento</h2><p>Inicie o reconhecimento por OCR</p></div><CloudUpload size={22}/></div>
          <label className="dropzone"><CloudUpload size={30}/><strong>{file ? file.name : 'Escolha um documento'}</strong><span>PDF, PNG, JPG, TIFF, WEBP ou TXT (teste) · Até 25 MB</span><input type="file" accept=".pdf,.png,.jpg,.jpeg,.tif,.tiff,.webp,.txt" onChange={e=>setFile(e.target.files?.[0] ?? null)}/></label>
          <label className="field-label">Lote (opcional)</label><select value={batchId} onChange={e=>setBatchId(e.target.value)}><option value="">Sem lote</option>{batches.filter(x=>x.status==='Open').map(b=><option key={b.id} value={b.id}>{b.name}</option>)}</select>
          <button className="primary full" disabled={!file || busy} onClick={() => void upload()}>{busy ? <LoaderCircle size={17} className="spin"/> : <ScanLine size={17}/>} Digitalizar e reconhecer</button>
          {selected && <><div className="divider"/><div className="panel-title"><div><h2>Documento processado</h2><p>{selected.fileName}</p></div><span className="chip">{selected.status}</span></div>
            <div className="preview"><FileText size={27}/><div><strong>{selected.fileName}</strong><span>{selected.ocrStatus} · {(selected.fileSize/1024).toFixed(1)} KB</span></div><a href={`${API_URL}/api/documents/${selected.id}/file`} onClick={e=>{e.preventDefault();void fetch(`${API_URL}/api/documents/${selected.id}/file`, {headers:{'X-Tenant-Id':DEMO_TENANT}}).then(r=>{if(!r.ok)throw new Error('Falha ao abrir arquivo');return r.blob()}).then(b=>{const url=URL.createObjectURL(b);window.open(url,'_blank','noopener,noreferrer');setTimeout(()=>URL.revokeObjectURL(url),60000)}).catch(err=>setError(String(err)))}}>Abrir</a></div>
            <label className="field-label">Texto reconhecido</label><pre className="ocr-text">{selected.ocrText || 'Nenhum texto reconhecido.'}</pre></>}
        </section>
        <section className="panel"><div className="panel-title"><div><h2>02. Ensinar ou aplicar modelo</h2><p>Campos 100% configuráveis</p></div><Settings2 size={21}/></div>
          {!selected ? <div className="empty"><ScanLine size={38}/><strong>Pronto para aprender</strong><p>Digitalize um documento para detectar os campos e criar um modelo reutilizável.</p></div> : <>
            {selected.documentTypeId && <span className="model-tag"><CheckCircle2 size={15}/> Modelo aplicado: {types.find(t=>t.id===selected.documentTypeId)?.name ?? 'Existente'}</span>}
            <label className="field-label">Aplicar um modelo já criado</label><div className="inline"><select value={chosenTemplate} onChange={e=>setChosenTemplate(e.target.value)}><option value="">Selecione um modelo</option>{types.map(t=><option key={t.id} value={t.id}>{t.name}</option>)}</select><button className="secondary" disabled={!chosenTemplate || busy || selected.ocrStatus!=='Completed'} onClick={()=>void applyTemplate(chosenTemplate)}>Aplicar</button></div>
            {current && <div className="corrections"><h3>Conferência e indexação</h3>{current.fields.map(f=><label className="control" key={f.key}><span>{f.label}{f.required?' *':''}</span><input value={corrections[f.key] ?? selected.values?.[f.key]?.value ?? ''} onChange={e=>setCorrections(s=>({...s,[f.key]:e.target.value}))}/></label>)}<button className="primary full" disabled={busy} onClick={()=>void confirmDocument()}><CheckCircle2 size={17}/> Confirmar indexação</button></div>}
            <div className="divider"/><h3>Criar novo modelo a partir deste documento</h3>
            <label className="control"><span>Nome do modelo</span><input placeholder="Ex.: Cheque bancário" value={modelName} onChange={e=>setModelName(e.target.value)}/></label>
            <label className="control"><span>Palavras-chave para reconhecer automaticamente</span><input placeholder="Ex.: cheque, banco, agência" value={keywords} onChange={e=>setKeywords(e.target.value)}/></label>
            <div className="suggestions"><div className="subheading">Campos sugeridos pelo OCR <span>{suggestions.length}</span></div>{suggestions.length===0 && <p className="muted">Nenhum campo conhecido detectado. Você pode adicionar campos manualmente.</p>}
              {fields.map((field,i)=><div className="field-row" key={i}><input className="field-key" value={field.key} placeholder="chave" onChange={e=>setFields(f=>f.map((x,j)=>j===i?{...x,key:e.target.value}:x))}/><input value={field.label} placeholder="Rótulo" onChange={e=>setFields(f=>f.map((x,j)=>j===i?{...x,label:e.target.value}:x))}/><label className="checkbox"><input type="checkbox" checked={field.required} onChange={e=>setFields(f=>f.map((x,j)=>j===i?{...x,required:e.target.checked}:x))}/>Obrig.</label><button className="icon-button" title="Remover campo" onClick={()=>setFields(s=>s.filter((_,j)=>j!==i))}><XCircle size={17}/></button></div>)}
              <button className="text-button" onClick={()=>setFields(s=>[...s,defaultField()])}><Plus size={16}/> Adicionar campo</button></div>
            <button className="primary full" disabled={busy || !modelName.trim() || fields.length===0} onClick={()=>void createType()}><Sparkles size={17}/> Salvar modelo dinâmico</button>
          </>}
        </section>
      </div>}
      {view === 'documents' && <section className="panel"><div className="panel-title"><div><h2>Biblioteca documental</h2><p>Busca por nome e texto completo OCR</p></div><FileSearch size={21}/></div><input placeholder="Pesquisar documentos..." value={search} onChange={e=>setSearch(e.target.value)}/><div className="items">{documents.filter(d=>(d.fileName+' '+d.ocrText).toLowerCase().includes(search.toLowerCase())).map(d=><button className="item" key={d.id} onClick={()=>openDoc(d)}><FileText size={20}/><span><strong>{d.fileName}</strong><small>{new Date(d.createdAt).toLocaleString('pt-BR')}</small></span><span className="chip">{d.status}</span><ChevronRight size={15}/></button>)}</div></section>}
      {view === 'models' && <section className="panel"><div className="panel-title"><div><h2>Catálogo de modelos</h2><p>Estruturas criadas sem código</p></div><Layers size={22}/></div><div className="items">{types.length===0&&<p className="muted">Nenhum modelo criado. Vá à Central de digitalização para ensinar o primeiro.</p>}{types.map(t=><div className="item" key={t.id}><Layers size={20}/><span><strong>{t.name}</strong><small>{t.fields.map(f=>f.label).join(' · ')}</small></span><span className="chip">v{t.version}</span></div>)}</div></section>}
      {view === 'batches' && <section className="panel"><div className="panel-title"><div><h2>Lotes de digitalização</h2><p>Organize o processamento em grupos</p></div><FolderOpen size={21}/></div><div className="inline"><input placeholder="Nome do novo lote" value={batchName} onChange={e=>setBatchName(e.target.value)}/><button className="primary" disabled={!batchName.trim()||busy} onClick={()=>void perform(async()=>{await api('/batches',{method:'POST',body:JSON.stringify({name:batchName})});setBatchName('')})}><Plus size={16}/> Criar lote</button></div><div className="items">{batches.map(b=><div className="item" key={b.id}><FolderOpen size={20}/><span><strong>{b.name}</strong><small>{documents.filter(d=>d.batchId===b.id).length} documento(s)</small></span><span className="chip">{b.status}</span>{b.status==='Open'&&<button className="secondary" disabled={busy} onClick={()=>void perform(async()=>{await api(`/batches/${b.id}/close`,{method:'POST'});setNotice('Lote finalizado')})}>Finalizar</button>}</div>)}</div></section>}
      {view === 'physical' && <div className="workspace"><section className="panel"><div className="panel-title"><div><h2>Localizações dinâmicas</h2><p>Depósito → Fileira → Estante → Prateleira</p></div><Archive size={21}/></div><label className="control"><span>Nome</span><input value={locationName} onChange={e=>setLocationName(e.target.value)} placeholder="Ex.: Corredor 03"/></label><label className="control"><span>Tipo</span><input value={locationKind} onChange={e=>setLocationKind(e.target.value)}/></label><label className="control"><span>Dentro de</span><select value={locationParent} onChange={e=>setLocationParent(e.target.value)}><option value="">Raiz</option>{locations.map(l=><option key={l.id} value={l.id}>{l.name}</option>)}</select></label><button className="primary full" disabled={!locationName.trim()||busy} onClick={()=>void perform(async()=>{await api('/locations',{method:'POST',body:JSON.stringify({name:locationName,kind:locationKind,parentId:locationParent||null})});setLocationName('')})}>Adicionar localização</button><div className="items">{locations.map(l=><div className="item" key={l.id}><Archive size={17}/><span><strong>{l.name}</strong><small>{l.kind} · {locations.find(p=>p.id===l.parentId)?.name||'Raiz'}</small></span></div>)}</div></section><section className="panel"><div className="panel-title"><div><h2>Caixas e rastreabilidade</h2><p>Vincule caixas a locais e documentos</p></div><Boxes size={21}/></div><label className="control"><span>Código da caixa</span><input value={boxCode} onChange={e=>setBoxCode(e.target.value)} placeholder="CX-00125"/></label><label className="control"><span>Localização</span><select value={boxLocation} onChange={e=>setBoxLocation(e.target.value)}><option value="">Selecione...</option>{locations.map(l=><option key={l.id} value={l.id}>{l.name}</option>)}</select></label><button className="primary full" disabled={!boxCode||!boxLocation||busy} onClick={()=>void perform(async()=>{await api('/boxes',{method:'POST',body:JSON.stringify({code:boxCode,locationId:boxLocation})});setBoxCode('')})}>Cadastrar caixa</button><div className="items">{boxes.map(b=><button className="item" key={b.id} onClick={()=>void perform(async()=>{const result=await api<{route:{name:string}[];code:string}>(`/boxes/${b.id}/route`);setRoute(result.route.map(r=>r.name).join(' → ')+' → '+result.code)})}><Boxes size={17}/><span><strong>{b.code}</strong><small>Ver rota física</small></span><ChevronRight size={15}/></button>)}</div>{route&&<div className="route">{route}</div>}{selected&&<><div className="divider"/><label className="control"><span>Vincular caixa a {selected.fileName}</span><select value={assignBoxId} onChange={e=>setAssignBoxId(e.target.value)}><option value="">Selecione...</option>{boxes.map(b=><option key={b.id} value={b.id}>{b.code}</option>)}</select></label><button className="secondary full" disabled={!assignBoxId||busy} onClick={()=>void perform(async()=>{const d=await api<GedDocument>(`/documents/${selected.id}/box`,{method:'POST',body:JSON.stringify({boxId:assignBoxId})});setSelected(d);setNotice('Caixa vinculada')})}>Associar documento</button></>}</section></div>}
      <footer>GED Dinâmico · MVP de desenvolvimento · Não expor na Internet sem autenticação e isolamento de tenant</footer>
    </main>
  </div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
