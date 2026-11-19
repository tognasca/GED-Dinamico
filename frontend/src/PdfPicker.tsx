import { useEffect, useRef, useState } from 'react';
import { getDocument, GlobalWorkerOptions, TextLayer } from 'pdfjs-dist';
import type { PDFDocumentProxy } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import 'pdfjs-dist/web/pdf_viewer.css';

GlobalWorkerOptions.workerSrc = workerUrl;

export default function PdfPicker({ url, field, onPick, onCancel }: {
  url: string;
  field: string;
  onPick: (text: string) => void;
  onCancel: () => void;
}) {
  const [pdf, setPdf] = useState<PDFDocumentProxy>();
  const [page, setPage] = useState(1);
  const [error, setError] = useState('');
  const [choice, setChoice] = useState('');
  const canvas = useRef<HTMLCanvasElement>(null);
  const layer = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let active = true;
    const task = getDocument({ url, isEvalSupported: false });
    void task.promise.then(document => { if (active) setPdf(document); })
      .catch(() => { if (active) setError('Não foi possível abrir o PDF para selecionar texto.'); });
    return () => { active = false; void task.destroy().catch(() => undefined); };
  }, [url]);

  useEffect(() => {
    if (!pdf || !canvas.current || !layer.current) return;
    const surface = canvas.current;
    const container = layer.current;
    let active = true;
    let render: ReturnType<Awaited<ReturnType<PDFDocumentProxy['getPage']>>['render']> | undefined;
    let textLayer: TextLayer | undefined;
    container.replaceChildren();
    void pdf.getPage(page).then(async current => {
      if (!active) return;
      const original = current.getViewport({ scale: 1 });
      const available = surface.closest(".pdf-picker-scroll")?.clientWidth || original.width;
      const scale = Math.min(1.4, available / original.width);
      const viewport = current.getViewport({ scale });
      const ratio = window.devicePixelRatio || 1;
      surface.width = Math.ceil(viewport.width * ratio);
      surface.height = Math.ceil(viewport.height * ratio);
      surface.style.width = `${viewport.width}px`;
      surface.style.height = `${viewport.height}px`;
      container.parentElement?.style.setProperty("--total-scale-factor", String(scale));
      render = current.render({ canvas: surface, viewport, transform: [ratio, 0, 0, ratio, 0, 0] });
      textLayer = new TextLayer({ container, viewport, textContentSource: current.streamTextContent() });
      await Promise.all([render.promise, textLayer.render()]);
      if (active && !container.textContent?.trim())
        setError('Esta página é uma imagem. Digite o valor no campo.');
    }).catch(() => { if (active) setError('Não foi possível carregar o texto desta página.'); });
    return () => { active = false; render?.cancel(); textLayer?.cancel(); };
  }, [pdf, page]);

  function changePage(next: number) {
    setChoice('');
    setError('');
    setPage(next);
  }

  function capture(target: EventTarget | null) {
    const selection = window.getSelection();
    const container = layer.current;
    if (!container || !(target instanceof HTMLElement)) return;
    container.querySelector('.picked-text')?.classList.remove('picked-text');
    if (selection && !selection.isCollapsed && container.contains(selection.anchorNode) &&
        container.contains(selection.focusNode)) {
      setChoice(selection.toString().trim());
    } else if (target.tagName === 'SPAN' && target.textContent?.trim()) {
      target.classList.add('picked-text');
      setChoice(target.textContent.trim());
    }
  }

  return <div className="review-file pdf-picker">
    <div className="inline">
      <button type="button" className="secondary" disabled={page <= 1} onClick={() => changePage(page - 1)}>Anterior</button>
      <span>Página {page}{pdf ? ` de ${pdf.numPages}` : ''}</span>
      <button type="button" className="secondary" disabled={!pdf || page >= pdf.numPages} onClick={() => changePage(page + 1)}>Próxima</button>
    </div>
    <p>Toque no texto ou selecione o trecho para preencher <strong>{field}</strong>.</p>
    {error && <p role="status">{error}</p>}
    <div className="pdf-picker-scroll">
      <div className="pdf-picker-sheet">
        <canvas ref={canvas}/>
        <div className="textLayer" ref={layer} onPointerUp={event => capture(event.target)} onContextMenu={event => capture(event.target)}/>
      </div>
    </div>
    {choice && <label className="control"><span>Valor selecionado</span>
      <input value={choice} onChange={event => setChoice(event.target.value)}/>
    </label>}
    <div className="inline">
      <button type="button" className="secondary" onClick={onCancel}>Voltar ao PDF</button>
      <button type="button" className="primary" disabled={!choice.trim() || !!error} onClick={() => onPick(choice.trim())}>Usar neste campo</button>
    </div>
  </div>;
}
