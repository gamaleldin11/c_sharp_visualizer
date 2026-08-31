/// <reference types="vite/client" />

declare module '*.css';

// Monaco ships its language contributions as plain JavaScript with no type declarations.
// They are imported purely for their side effect of registering a grammar, so an empty module
// declaration is the whole contract.
declare module 'monaco-editor/languages/definitions/*';
