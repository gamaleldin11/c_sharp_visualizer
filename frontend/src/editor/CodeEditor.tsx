import { useEffect, useRef } from 'react';
import Editor, { type OnMount } from '@monaco-editor/react';
// Imported for its side effect: points the loader at our bundled Monaco, not a CDN.
import './monacoSetup';
import { useApp } from '../state/store';

type Mon = Parameters<OnMount>[1];
type Ed = Parameters<OnMount>[0];

export function CodeEditor({ activeLine }: { activeLine: number | null }) {
  const { source, setSource, theme } = useApp();
  const editorRef = useRef<Ed | null>(null);
  const monacoRef = useRef<Mon | null>(null);
  const decorations = useRef<ReturnType<Ed['createDecorationsCollection']> | null>(null);

  const onMount: OnMount = (editor, monaco) => {
    editorRef.current = editor;
    monacoRef.current = monaco;
    decorations.current = editor.createDecorationsCollection([]);
  };

  useEffect(() => {
    const editor = editorRef.current;
    const monaco = monacoRef.current;
    if (!editor || !monaco || !decorations.current) return;

    if (activeLine === null) {
      decorations.current.set([]);
      return;
    }

    decorations.current.set([
      {
        range: new monaco.Range(activeLine, 1, activeLine, 1),
        options: {
          isWholeLine: true,
          className: 'line-executed',
          glyphMarginClassName: 'glyph-executed',
        },
      },
    ]);
    editor.revealLineInCenterIfOutsideViewport(activeLine);
  }, [activeLine]);

  return (
    <Editor
      height="100%"
      defaultLanguage="csharp"
      theme={theme === 'light' ? 'vs' : 'vs-dark'}
      value={source}
      onChange={(v) => setSource(v ?? '')}
      onMount={onMount}
      options={{
        minimap: { enabled: false },
        fontSize: 13,
        lineNumbers: 'on',
        glyphMargin: true,
        scrollBeyondLastLine: false,
        automaticLayout: true,
        tabSize: 4,
        renderLineHighlight: 'none',
      }}
    />
  );
}
