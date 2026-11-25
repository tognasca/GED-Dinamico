import { useEffect, useRef, useState } from 'react';
import { getDocument, GlobalWorkerOptions, type RenderTask } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';

GlobalWorkerOptions.workerSrc = workerUrl;

export default function PdfEvidence({ url, page, fileName }: { url: string; page: number; fileName: string }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');

  useEffect(() => {
    const surface = canvas.current;
    if (!surface) return;
    let active = true;
    let render: RenderTask | undefined;
    const task = getDocument({ url, isEvalSupported: false });
    void task.promise.then(pdf => pdf.getPage(page)).then(async pdfPage => {
      if (!active) return;
      const viewport = pdfPage.getViewport({ scale: 2 });
      surface.width = Math.ceil(viewport.width);
      surface.height = Math.ceil(viewport.height);
      render = pdfPage.render({ canvas: surface, viewport });
      await render.promise;
      if (active) setStatus('ready');
    }).catch(() => { if (active) setStatus('error'); });
    return () => {
      active = false;
      render?.cancel();
      void task.destroy().catch(() => undefined);
    };
  }, [url, page]);

  return <>
    {status === 'loading' && <p role="status">Abrindo página {page}…</p>}
    {status === 'error' && <p role="alert">Não foi possível abrir a página. <a href={url} download={fileName}>Baixar o original</a></p>}
    <canvas ref={canvas} hidden={status !== 'ready'} role="img" aria-label={`Página ${page} de ${fileName}`}/>
  </>;
}
