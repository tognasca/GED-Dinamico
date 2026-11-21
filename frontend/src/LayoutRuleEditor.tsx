import { lazy, Suspense, useEffect, useId, useRef, useState } from 'react';
import { api, type GedDocument, type Field } from './api';
import DocumentPreview, { useDocumentFile } from './DocumentPreview';

const PdfPicker = lazy(() => import('./PdfPicker'));
type Props = {
  document: GedDocument; documents: GedDocument[]; field: Field;
  version: number; sourceUrl: string; currentValue: string; onSaved: () => void;
};

export default function LayoutRuleEditor(props: Props) {
  const [open, setOpen] = useState(false);
  return <>
    <button type="button" className="text-button teach-field" onClick={() => setOpen(true)}>
      Ensinar leitura de {props.field.label}
    </button>
    {open && <TeachingWizard key={`${props.document.id}:${props.field.key}`} {...props} onClose={() => setOpen(false)}/>}
  </>;
}

function TeachingWizard({ document, documents, field, version, sourceUrl, currentValue, onSaved, onClose }: Props & { onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  const [step, setStep] = useState(1);
  const [marker, setMarker] = useState('');
  const [anchor, setAnchor] = useState(field.anchor || field.label);
  const [picking, setPicking] = useState<'marker' | 'anchor'>();
  const [testId, setTestId] = useState('');
  const [expected, setExpected] = useState('');
  const [preview, setPreview] = useState<{ value: string; signature: string }>();
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const testFiles = documents.filter(item => item.id !== document.id && item.ocrStatus === 'Completed');
  const testDocument = testFiles.find(item => item.id === testId);
  const testFile = useDocumentFile(testDocument);
  const signature = JSON.stringify([version, marker.trim(), anchor.trim(), testId, field.key]);
  const validPreview = preview?.signature === signature ? preview : undefined;
  const matches = !!validPreview?.value && validPreview.value === expected.trim();
  const steps = ['Escolher informação', 'Marcar no documento', 'Testar em outro arquivo', 'Confirmar'];

  useEffect(() => { dialog.current?.showModal(); }, []);
  useEffect(() => { heading.current?.focus(); }, [step]);
  useEffect(() => { setConfirmed(false); }, [signature, expected]);

  function go(next: number) { setMessage(''); setPicking(undefined); setStep(next); }

  async function submit(publish: boolean) {
    if (publish && (!matches || !confirmed)) return;
    setBusy(true);
    setMessage('');
    try {
      const result = await api<{ value: string }>(`/types/${document.documentTypeId}/layout-rule`, {
        method: 'POST',
        body: JSON.stringify({ version, documentId: document.id, testDocumentId: testId,
          fieldKey: field.key, marker: marker.trim(), anchor: anchor.trim(), publish, expected: expected.trim() }),
      });
      if (publish) { setStep(5); onSaved(); }
      else { setPreview({ value: result.value, signature }); setConfirmed(false); setStep(4); }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Não foi possível concluir. Tente novamente.');
    } finally { setBusy(false); }
  }

  return <dialog ref={dialog} className="teaching-dialog" aria-labelledby={titleId}
    onCancel={event => { event.preventDefault(); if (!busy) onClose(); }}>
    <header className="teaching-header"><div><p>Ensinar uma leitura automática</p>
      <h2 id={titleId}>{field.label}</h2></div>
      <button type="button" className="secondary" disabled={busy} onClick={onClose} aria-label="Fechar assistente">Fechar</button>
    </header>
    {step <= 4 && <ol className="teaching-steps" aria-label="Etapas do assistente">{steps.map((label, index) =>
      <li key={label} aria-current={step === index + 1 ? 'step' : undefined}><span>{index + 1}</span>{label}</li>)}</ol>}
    <section className="teaching-body" aria-busy={busy}>
      <h3 ref={heading} tabIndex={-1}>{step === 5 ? 'Leitura ensinada' : steps[step - 1]}</h3>
      {step === 1 && <>
        <p>Vamos ensinar onde o GED encontra <strong>{field.label}</strong> nos próximos documentos deste formato.</p>
        <dl className="teaching-summary"><dt>Documento de exemplo</dt><dd>{document.fileName}</dd>
          <dt>Informação escolhida</dt><dd>{field.label}</dd><dt>Valor neste documento</dt><dd>{currentValue || 'Ainda não preenchido'}</dd></dl>
        <p>Você vai indicar o texto impresso ao lado do valor e conferir a leitura em outro arquivo. O documento atual continua precisando ser salvo na conferência.</p>
        <button type="button" className="primary" onClick={() => go(2)}>Começar com este campo</button>
      </>}
      {step === 2 && <>
        <p>Marque o <strong>nome impresso ao lado da informação</strong>, por exemplo “Placa do veículo”. Depois marque um texto que se repete nesse formato, como o nome da empresa.</p>
        <div className="teaching-grid"><div className="teaching-document">
          {picking && sourceUrl && document.contentType === 'application/pdf' ? <Suspense fallback={<p>Abrindo seleção…</p>}>
            <PdfPicker url={sourceUrl} documentId={document.id} field={field.label}
              instruction={picking === 'anchor' ? 'Marque o rótulo impresso ao lado do valor, sem selecionar o valor.' : 'Marque um texto fixo que identifica este formato, como o nome da empresa.'}
              confirmLabel={picking === 'anchor' ? 'Usar como rótulo' : 'Usar para reconhecer este formato'}
              onCancel={() => setPicking(undefined)} onPick={text => {
                if (picking === 'anchor') setAnchor(text); else setMarker(text);
                setPicking(undefined);
              }}/>
          </Suspense> : <DocumentPreview document={document} url={sourceUrl}/>}
        </div><fieldset className="teaching-controls">
          <h4>1. Onde está a informação?</h4><p>Selecione o rótulo, como “Placa”, e não o valor, como “TST1A10”.</p>
          {document.contentType === 'application/pdf' && <button type="button" className="secondary" disabled={!sourceUrl} onClick={() => setPicking('anchor')}>Marcar rótulo no PDF</button>}
          <p>Rótulo escolhido: <strong>{anchor || 'Nenhum'}</strong></p>
          <h4>2. Como reconhecer este formato?</h4><p>Escolha um texto fixo presente também no arquivo que será usado para testar.</p>
          {document.contentType === 'application/pdf' && <button type="button" className="secondary" disabled={!sourceUrl} onClick={() => setPicking('marker')}>Marcar texto de identificação</button>}
          <p>Texto escolhido: <strong>{marker || 'Nenhum'}</strong></p>
          {document.contentType !== 'application/pdf' && <p>Confira a imagem ou o texto reconhecido e preencha os textos nas opções abaixo.</p>}
          <details open={document.contentType !== 'application/pdf'}><summary>Opções avançadas e ajuste manual</summary>
            <label className="control"><span>Rótulo ao lado do valor</span><input maxLength={200} value={anchor} onChange={e => setAnchor(e.target.value)}/></label>
            <label className="control"><span>Texto que identifica este formato</span><input maxLength={200} value={marker} onChange={e => setMarker(e.target.value)}/></label>
          </details>
        </fieldset></div>
        {picking && <p role="status">Finalize a seleção ou volte ao original para continuar.</p>}
        <footer className="teaching-actions"><button type="button" className="secondary" onClick={() => go(1)}>Voltar</button>
          <button type="button" className="primary" disabled={!!picking || anchor.trim().length < 2 || marker.trim().length < 2 || anchor.trim().length > 200 || marker.trim().length > 200} onClick={() => go(3)}>Testar em outro arquivo</button></footer>
      </>}
      {step === 3 && <>
        <p>Escolha <strong>outro documento do mesmo formato</strong>, com valores diferentes. Renomear uma cópia do mesmo arquivo não serve como teste.</p>
        {!testFiles.length && <p role="status">Importe outro arquivo e aguarde a leitura antes de ensinar. Feche o assistente para fazer a importação.</p>}
        <fieldset disabled={busy}><label className="control"><span>Arquivo para testar</span>
          <select value={testId} onChange={e => { setTestId(e.target.value); setExpected(''); setPreview(undefined); }}>
            <option value="">Escolha outro arquivo</option>{testFiles.map(item => <option key={item.id} value={item.id}>{item.fileName}</option>)}
          </select></label>
          {testDocument && <div className="teaching-grid"><DocumentPreview document={testDocument} url={testFile?.url} error={testFile?.error}/>
            <label className="control"><span>Qual é o valor de {field.label} neste arquivo?</span>
              <input value={expected} onChange={e => setExpected(e.target.value)}/>
              <small>Olhe o original e digite o valor correto. Assim podemos comparar com a leitura automática.</small></label></div>}
          <footer className="teaching-actions"><button type="button" className="secondary" onClick={() => go(2)}>Voltar</button>
            <button type="button" className="primary" disabled={!testDocument || !expected.trim() || !testFile?.url} onClick={() => void submit(false)}>{busy ? 'Verificando…' : 'Verificar leitura'}</button></footer>
        </fieldset>
      </>}
      {step === 4 && <>
        <p>Compare o resultado com o arquivo de teste antes de confirmar.</p>
        <div className="teaching-grid">{testDocument && <DocumentPreview document={testDocument} url={testFile?.url} error={testFile?.error}/>}
          <div><dl className="teaching-summary"><dt>Você informou</dt><dd>{expected}</dd><dt>O GED encontrou</dt><dd>{validPreview?.value || 'Nenhum valor encontrado'}</dd></dl>
          <p role="status">{matches ? 'Os valores coincidem. Confira o original para confirmar.' : 'A leitura ainda precisa de ajuste. Volte para corrigir a marcação ou o valor de teste.'}</p>
          {matches && <label className="checkbox"><input type="checkbox" disabled={busy} checked={confirmed} onChange={e => setConfirmed(e.target.checked)}/>Conferi no original e o valor está correto</label>}</div>
        </div>
        <footer className="teaching-actions"><button type="button" className="secondary" disabled={busy} onClick={() => go(2)}>Ajustar marcação</button>
          <button type="button" className="secondary" disabled={busy} onClick={() => go(3)}>Revisar teste</button>
          <button type="button" className="primary" disabled={busy || !matches || !confirmed} onClick={() => void submit(true)}>{busy ? 'Salvando…' : 'Confirmar para os próximos documentos'}</button></footer>
      </>}
      {step === 5 && <><p>A regra de <strong>{field.label}</strong> foi salva para os próximos documentos deste formato. Continue conferindo os valores antes de salvar.</p>
        <p>Os documentos já salvos continuam como estavam. Para guardar as correções do documento aberto, use <strong>Salvar na biblioteca</strong> ao voltar à conferência.</p>
        <button type="button" className="primary" onClick={onClose}>Voltar à conferência</button></>}
      {message && <p className="teaching-error" role="alert">{message}</p>}
    </section>
  </dialog>;
}
