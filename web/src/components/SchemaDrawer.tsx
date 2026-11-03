"use client";

import { FileUp, KeyRound, Loader2, ShieldCheck, Trash2 } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { useStore } from "@/lib/store";
import type { SchemaWorkerResponse } from "@/workers/schema.worker";

const MAX_LISTED = 200;

export function SchemaDrawer({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { schema, schemaInfo, dialect, setSchema } = useStore();
  const [parsing, setParsing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);
  const [filter, setFilter] = useState("");
  const [opened, setOpened] = useState<Set<string>>(new Set());

  const load = (file: File) => {
    if (!/\.(sql|json)$/i.test(file.name)) return setError("Use a .sql DDL script or a .json schema file.");
    setParsing(true);
    setError(null);
    const worker = new Worker(new URL("../workers/schema.worker.ts", import.meta.url), { type: "module" });
    worker.onmessage = (e: MessageEvent<SchemaWorkerResponse>) => {
      const r = e.data;
      if (r.ok) setSchema(r.schema, { fileName: file.name, ms: r.ms, statements: r.statements, skipped: r.skipped });
      else setError(r.error);
      setParsing(false);
      worker.terminate();
    };
    worker.onerror = (e) => {
      setError(e.message || "Schema worker failed.");
      setParsing(false);
      worker.terminate();
    };
    worker.postMessage({ file, dialect });
  };

  const tables = schema ? Object.entries(schema.tables) : [];
  const needle = filter.trim().toLowerCase();
  const matches = needle ? tables.filter(([key]) => key.toLowerCase().includes(needle)) : tables;

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="left" className="w-full gap-0 sm:max-w-md">
        <SheetHeader className="border-b">
          <SheetTitle>Schema</SheetTitle>
          <SheetDescription className="flex items-start gap-1.5">
            <ShieldCheck className="mt-0.5 size-4 shrink-0 text-emerald-600 dark:text-emerald-400" />
            Parsed in your browser. The file is never uploaded; only metadata of tables your query mentions is sent with each analysis.
          </SheetDescription>
        </SheetHeader>

        <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-4">
          <label
            onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => { e.preventDefault(); setDragging(false); const f = e.dataTransfer.files[0]; if (f) load(f); }}
            className={`flex cursor-pointer flex-col items-center gap-2 rounded-lg border-2 border-dashed p-6 text-center text-sm transition-colors focus-within:ring-2 focus-within:ring-ring ${dragging ? "border-primary bg-muted" : "hover:bg-muted/50"}`}
          >
            {parsing ? <Loader2 className="size-6 animate-spin" /> : <FileUp className="size-6 text-muted-foreground" />}
            <span><span className="font-medium">Drop a .sql or .json file</span> or click to browse</span>
            <span className="text-xs text-muted-foreground">DDL is read as {dialect}</span>
            <input type="file" accept=".sql,.json" className="sr-only" onChange={(e) => { const f = e.target.files?.[0]; if (f) load(f); e.target.value = ""; }} />
          </label>

          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}

          {schema && schemaInfo && (
            <>
              <div className="flex items-start gap-2 text-sm">
                <div className="mr-auto min-w-0">
                  <p className="truncate font-medium">{schemaInfo.fileName}</p>
                  <p className="text-xs text-muted-foreground">
                    {tables.length} tables · {schemaInfo.ms.toFixed(0)} ms
                    {schemaInfo.skipped > 0 && <> · {schemaInfo.skipped} of {schemaInfo.statements} statements could not be parsed</>}
                  </p>
                </div>
                <Button variant="ghost" size="sm" onClick={() => setSchema(null, null)}><Trash2 /> Clear</Button>
              </div>

              {tables.length > 10 && (
                <input
                  type="search"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder="Filter tables"
                  aria-label="Filter tables"
                  className="h-8 rounded-lg border bg-background px-2 text-sm"
                />
              )}

              {/* ponytail: capped list instead of virtualization; filter narrows huge schemas. */}
              <ul className="space-y-1">
                {matches.slice(0, MAX_LISTED).map(([key, t]) => (
                  <li key={key}>
                    <details
                      className="rounded-md border"
                      onToggle={(e) => {
                        const open = e.currentTarget.open;
                        setOpened((s) => { const n = new Set(s); if (open) n.add(key); else n.delete(key); return n; });
                      }}
                    >
                      <summary className="flex cursor-pointer items-center gap-2 px-3 py-2 text-sm">
                        <span className="mr-auto font-medium">{key}</span>
                        <span className="text-xs text-muted-foreground">{Object.keys(t.columns).length} cols · {t.indexes.length} idx</span>
                      </summary>
                      {opened.has(key) && <div className="space-y-3 border-t px-3 py-2 text-xs">
                        <table className="w-full">
                          <tbody>
                            {Object.values(t.columns).map((c) => (
                              <tr key={c.name}>
                                <td className="py-0.5 font-mono">
                                  {c.isPrimaryKey && <KeyRound className="mr-1 inline size-3 text-amber-500" aria-label="Primary key" />}
                                  {c.name}
                                </td>
                                <td className="py-0.5 text-muted-foreground">{c.dataType}{c.isNullable ? " · null" : ""}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                        {t.indexes.length > 0 && (
                          <ul className="space-y-1">
                            {t.indexes.map((i) => (
                              <li key={i.indexName} className="font-mono">
                                <span className="text-muted-foreground">{i.isUnique ? "UNIQUE " : ""}</span>{i.indexName} ({i.keyColumns.join(", ")})
                                {i.includeColumns.length > 0 && <span className="text-muted-foreground"> INCLUDE ({i.includeColumns.join(", ")})</span>}
                              </li>
                            ))}
                          </ul>
                        )}
                      </div>}
                    </details>
                  </li>
                ))}
              </ul>
              {matches.length > MAX_LISTED && (
                <p className="text-center text-xs text-muted-foreground">
                  Showing {MAX_LISTED} of {matches.length} tables. Filter to find others; all of them stay active for analysis and IntelliSense.
                </p>
              )}
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
