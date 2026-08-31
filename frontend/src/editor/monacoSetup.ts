import * as monaco from 'monaco-editor/editor/editor.api';
import { loader } from '@monaco-editor/react';

// Only the language this app can actually visualize. The package index registers every
// grammar Monaco ships - about eighty of them - to highlight the one we need.
import 'monaco-editor/languages/definitions/csharp/register';

// The package exports map rewrites monaco-editor/<path> to esm/vs/<path>, which is why these
// specifiers omit the esm/vs prefix that appears in the on-disk layout.
import EditorWorker from 'monaco-editor/editor/editor.worker?worker';

/**
 * Serves Monaco from our own bundle instead of a CDN.
 *
 * @monaco-editor/react defaults to fetching Monaco from jsdelivr at runtime. That is three
 * problems for a production deployment: the editor breaks with no network or behind a
 * restrictive CSP, every visitor's browser announces itself to a third party, and the app
 * silently depends on a version nobody pinned.
 *
 * Bundling it costs about 1.5MB, kept in its own chunk so it never blocks first paint.
 */
self.MonacoEnvironment = {
  // Monaco runs its tokenizer and language services off the main thread. Vite compiles this
  // worker as part of the build, so it is served from our origin like everything else.
  getWorker() {
    return new EditorWorker();
  },
};

loader.config({ monaco });

/** Started eagerly so the editor is ready by the time the user has read the page. */
export const monacoReady = loader.init();
