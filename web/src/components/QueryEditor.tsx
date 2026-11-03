"use client";

import Editor, { type Monaco, type OnMount } from "@monaco-editor/react";
import type { editor, Position } from "monaco-editor";
import { useEffect, useRef, useState } from "react";
import { inspectLinq } from "@/lib/linq-inspector";
import { relevantSchema, useStore } from "@/lib/store";
import type { CompactSchemaMetadataDTO, Diagnostic } from "@/lib/types";

type CodeEditor = Parameters<OnMount>[0];

let activeEditor: CodeEditor | null = null;

/** Moves the cursor to a diagnostic position (used by the results panel). */
export function revealInEditor(line: number, column: number) {
  activeEditor?.revealLineInCenter(line);
  activeEditor?.setPosition({ lineNumber: line, column });
  activeEditor?.focus();
}

export function QueryEditor({ dark }: { dark: boolean }) {
  const { queryType, dialect, query, response, analyzedQuery, set } = useStore();
  const editorRef = useRef<CodeEditor | null>(null);
  const monacoRef = useRef<Monaco | null>(null);
  const [mounted, setMounted] = useState(false);
  const backendValid = !!response && analyzedQuery === query;

  const onMount: OnMount = (editor, monaco) => {
    editorRef.current = activeEditor = editor;
    monacoRef.current = monaco;
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => useStore.getState().analyze());
    setMounted(true);
  };

  // Real-time LINQ hints while typing (client-side, no API call). Backend results for the same text replace them.
  useEffect(() => {
    const monaco = monacoRef.current;
    const model = editorRef.current?.getModel();
    if (!monaco || !model) return;
    if (queryType !== "LINQ" || backendValid) {
      monaco.editor.setModelMarkers(model, "linq-inspector", []);
      return;
    }
    const timer = setTimeout(() => {
      monaco.editor.setModelMarkers(model, "linq-inspector", inspectLinq(model.getValue()).map((h) => ({
        severity: monaco.MarkerSeverity.Warning, code: h.code, message: h.message,
        startLineNumber: h.line, startColumn: h.column, endLineNumber: h.line, endColumn: h.endColumn,
      })));
    }, 300);
    return () => clearTimeout(timer);
  }, [query, queryType, backendValid, mounted]);

  // Backend diagnostics become squiggles; they are dropped once the text no longer matches what was analyzed.
  useEffect(() => {
    const editor = editorRef.current;
    const monaco = monacoRef.current;
    const model = editor?.getModel();
    if (!editor || !monaco || !model) return;
    const valid = backendValid && response;
    const toMarker = (d: Diagnostic, severity: number) => {
      const line = Math.min(Math.max(d.line, 1), model.getLineCount());
      const word = model.getWordAtPosition({ lineNumber: line, column: d.column });
      return { severity, message: d.message, code: d.code, startLineNumber: line, startColumn: d.column, endLineNumber: line, endColumn: d.endColumn ?? word?.endColumn ?? d.column + 1 };
    };
    monaco.editor.setModelMarkers(model, "query-index-advisor", valid
      ? [...response.errors.map((d) => toMarker(d, monaco.MarkerSeverity.Error)), ...response.warnings.map((d) => toMarker(d, monaco.MarkerSeverity.Warning))]
      : []);
  }, [response, backendValid, mounted]);

  return (
    <Editor
      height="100%"
      language={queryType === "LINQ" ? "csharp" : SQL_LANGUAGE[dialect]}
      theme={dark ? "vs-dark" : "light"}
      value={query}
      onChange={(v) => set({ query: v ?? "" })}
      beforeMount={registerSchemaCompletion}
      onMount={onMount}
      options={{ minimap: { enabled: false }, fontSize: 14, automaticLayout: true, scrollBeyondLastLine: false, padding: { top: 12 }, tabSize: 2 }}
    />
  );
}

const SQL_LANGUAGE = { SQLSERVER: "sql", POSTGRESQL: "pgsql", MYSQL: "mysql" } as const;

let registered = false;

/** IntelliSense from the in-memory schema (CF-03). Registered once; reads the live schema on every request. */
function registerSchemaCompletion(monaco: Monaco) {
  if (registered) return;
  registered = true;
  for (const language of [...Object.values(SQL_LANGUAGE), "csharp"]) {
    monaco.languages.registerCompletionItemProvider(language, {
      triggerCharacters: ["."],
      provideCompletionItems(model: editor.ITextModel, position: Position) {
        const schema = useStore.getState().schema;
        if (!schema) return { suggestions: [] };
        const word = model.getWordUntilPosition(position);
        const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn };
        const before = model.getValueInRange({ startLineNumber: position.lineNumber, startColumn: 1, endLineNumber: position.lineNumber, endColumn: word.startColumn });
        const tables = Object.values(schema.tables);
        const { Field, Class } = monaco.languages.CompletionItemKind;

        const columnsOf = (t: (typeof tables)[number]) =>
          Object.values(t.columns).map((c) => ({ label: c.name, kind: Field, detail: `${t.tableName} · ${c.dataType}${c.isPrimaryKey ? " · PK" : ""}`, insertText: c.name, range }));

        const qualifier = /([\w$#]+)[\]"`]?\.$/.exec(before)?.[1];
        if (qualifier) {
          const tableName = aliases(model.getValue())[qualifier.toLowerCase()] ?? qualifier;
          const table = findTable(schema, tableName);
          if (table) return { suggestions: columnsOf(table) };
          const prefix = qualifier.toLowerCase() + ".";
          const inSchema = Object.entries(schema.tables).filter(([key]) => key.toLowerCase().startsWith(prefix)).map(([, t]) => t);
          if (inSchema.length) return { suggestions: inSchema.map((t) => ({ label: t.tableName, kind: Class, insertText: t.tableName, range })) };
        }
        // Columns only for tables the query already mentions; offering every column of a large schema floods the list.
        const mentioned = Object.values(relevantSchema(schema, model.getValue())?.tables ?? {});
        return {
          suggestions: [
            ...tables.map((t) => ({ label: t.tableName, kind: Class, detail: `table · ${Object.keys(t.columns).length} columns`, insertText: t.tableName, range })),
            ...mentioned.flatMap(columnsOf),
          ],
        };
      },
    });
  }
}

const NOT_ALIAS = new Set(["where", "on", "join", "inner", "left", "right", "full", "cross", "outer", "group", "order", "set", "with", "union", "having", "as"]);

/** alias -> table, from SQL `FROM/JOIN t alias` and LINQ `t.Where(x =>` / `from x in db.t`. */
function aliases(text: string): Record<string, string> {
  const map: Record<string, string> = {};
  const base = (name: string) => name.split(".").pop()!.replace(/[[\]"`]/g, "");
  for (const m of text.matchAll(/\b(?:from|join|update)\s+([\w.[\]"`$#]+)(?:\s+(?:as\s+)?([a-z_]\w*))?/gi))
    if (m[2] && !NOT_ALIAS.has(m[2].toLowerCase())) map[m[2].toLowerCase()] = base(m[1]);
  for (const m of text.matchAll(/\.(\w+)\s*\.\s*\w+\s*\(\s*(\w+)\s*=>/g)) map[m[2].toLowerCase()] = m[1];
  for (const m of text.matchAll(/\bfrom\s+(\w+)\s+in\s+[\w.]*?(\w+)\b/g)) map[m[1].toLowerCase()] = m[2];
  return map;
}

function findTable(schema: CompactSchemaMetadataDTO, name: string) {
  const n = name.toLowerCase();
  return Object.values(schema.tables).find((t) => t.tableName.toLowerCase() === n);
}
