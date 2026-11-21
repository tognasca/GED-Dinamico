# Ensinar a leitura de um campo

O uso diário continua sendo importar, conferir e salvar. O assistente é uma tarefa de configuração e aparece no botão **Ensinar leitura de [nome do campo]**, na conferência. Ele não exige corrigir um campo apenas para tornar a opção visível.

## Antes de começar

Tenha um tipo de documento aplicado e dois arquivos do mesmo formato com leitura concluída e conteúdo diferente. O segundo arquivo deve estar disponível na lista carregada pelo sistema. Se não aparecer, importe-o e abra novamente o documento de exemplo. Uma cópia idêntica renomeada é rejeitada pelo servidor.

## As quatro etapas

1. **Escolher informação** — confirme o campo e o arquivo de exemplo. O valor exibido é uma referência do documento aberto.
2. **Marcar no documento** — use **Marcar rótulo no PDF** para selecionar o nome impresso perto do valor, por exemplo “Placa do veículo”. Depois use **Marcar texto de identificação** para selecionar um texto fixo deste formato, por exemplo o nome da empresa. O valor variável da placa não deve ser usado como identificador. PDFs escaneados mantêm o reconhecimento de palavras da página. Em imagens e arquivos de texto, confira o original e use **Opções avançadas e ajuste manual**. TIFF permite baixar o original.
3. **Testar em outro arquivo** — escolha um segundo arquivo. O original aparece para conferência. Digite o valor correto desse arquivo e clique em **Verificar leitura**. Isso ainda não salva uma regra.
4. **Confirmar** — compare o valor informado com o encontrado pelo GED. Se não coincidirem, ajuste a marcação ou revise o teste. Se coincidirem, marque **Conferi no original e o valor está correto** e confirme para os próximos documentos.

A confirmação publica a regra do campo para o formato identificado. As correções do documento aberto continuam precisando de **Salvar na biblioteca**. Documentos já indexados não são alterados em massa. A versão do modelo, a presença do identificador nos dois arquivos e a diferença entre os conteúdos continuam sendo validadas pelo servidor.

## Computador e dispositivos móveis

O assistente abre em uma janela com navegação própria. No computador, o original e os controles ficam lado a lado; em telas menores ficam empilhados. No smartphone a janela ocupa a tela. O foco acompanha a etapa e Escape fecha o assistente quando não há gravação em andamento. Fechar descarta a configuração ainda não publicada; uma regra já confirmada permanece salva.

## Verificação técnica

Na pasta `frontend`:

```sh
npm ci
npm test
npm run build
```

Os testes usam React e JSDOM para verificar seleção de rótulo e identificador, teste com outro arquivo, comparação, confirmação obrigatória, versão alterada, falta de arquivo e erros de gravação. A renderização do seletor PDF é substituída nos testes do assistente. Esses testes não substituem homologação visual nem OCR real.

Roteiro de homologação: repetir o fluxo com um PDF digital, um PDF escaneado e uma imagem; testar largura de 390, 768 e 1440 pixels; conferir teclado, rolagem, fechamento e ausência de transbordamento horizontal; importar um terceiro documento do mesmo formato para verificar a regra; conferir também salvar e buscar um documento sem abrir o assistente.

Não foram alterados banco, endpoints, portas ou regras de extração. O reconhecimento continua baseado em texto identificador e rótulo; marcar texto não cria um modelo de aprendizado de máquina.
