"use client";

import { Database, DatabaseZap, History, Loader2, Moon, Play, Sun } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { HistoryDrawer } from "@/components/HistoryDrawer";
import { QueryEditor } from "@/components/QueryEditor";
import { ResultsPanel } from "@/components/ResultsPanel";
import { SchemaDrawer } from "@/components/SchemaDrawer";
import { useStore } from "@/lib/store";
import type { Dialect, QueryType } from "@/lib/types";

const MODES: { value: QueryType; label: string }[] = [
  { value: "SQL", label: "Raw SQL" },
  { value: "LINQ", label: "C# LINQ" },
];
const DIALECTS: { value: Dialect; label: string }[] = [
  { value: "SQLSERVER", label: "SQL Server" },
  { value: "POSTGRESQL", label: "PostgreSQL" },
  { value: "MYSQL", label: "MySQL" },
];

export function Workspace() {
  const { queryType, dialect, query, schema, history, loading, set, analyze } = useStore();
  const [panel, setPanel] = useState<"schema" | "history" | null>(null);
  const [dark, setDark] = useState(() => document.documentElement.classList.contains("dark"));

  useEffect(() => {
    document.documentElement.classList.toggle("dark", dark);
    try { localStorage.setItem("theme", dark ? "dark" : "light"); } catch { /* storage blocked */ }
  }, [dark]);

  const tableCount = schema ? Object.keys(schema.tables).length : 0;

  return (
    <div className="flex h-dvh flex-col">
      <header className="flex flex-wrap items-center gap-2 border-b px-3 py-2">
        <h1 className="mr-2 flex items-center gap-2 font-semibold">
          <DatabaseZap className="size-5" /> QueryIndex Advisor
        </h1>

        <div role="radiogroup" aria-label="Input mode" className="flex rounded-lg border p-0.5">
          {MODES.map((m) => (
            <button
              key={m.value}
              type="button"
              role="radio"
              aria-checked={queryType === m.value}
              onClick={() => set({ queryType: m.value })}
              className={`rounded-md px-2.5 py-1 text-sm ${queryType === m.value ? "bg-primary text-primary-foreground" : "hover:bg-muted"}`}
            >
              {m.label}
            </button>
          ))}
        </div>

        <label className="flex items-center gap-1.5 text-sm">
          <span className="sr-only sm:not-sr-only text-muted-foreground">Target</span>
          <select
            value={dialect}
            onChange={(e) => set({ dialect: e.target.value as Dialect })}
            className="h-8 rounded-lg border bg-background px-2 text-sm"
          >
            {DIALECTS.map((d) => (
              <option key={d.value} value={d.value}>{d.label}</option>
            ))}
          </select>
        </label>

        <div className="ml-auto flex flex-wrap items-center gap-1">
          <Button variant="ghost" size="sm" onClick={() => setPanel("schema")}>
            <Database /> Schema{tableCount > 0 && <span className="rounded bg-emerald-500/15 px-1.5 text-xs text-emerald-700 dark:text-emerald-300">{tableCount}</span>}
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setPanel("history")}>
            <History /> History{history.length > 0 && <span className="rounded bg-muted px-1.5 text-xs">{history.length}</span>}
          </Button>
          <Button variant="ghost" size="icon-sm" aria-label={dark ? "Switch to light theme" : "Switch to dark theme"} onClick={() => setDark(!dark)}>
            {dark ? <Sun /> : <Moon />}
          </Button>
          <Button size="sm" onClick={analyze} disabled={loading || !query.trim()} title="Ctrl + Enter">
            {loading ? <Loader2 className="animate-spin" /> : <Play />} Analyze<span className="hidden sm:inline">&nbsp;&amp; Recommend</span>
          </Button>
        </div>
      </header>

      <main className="grid min-h-0 flex-1 grid-cols-1 grid-rows-2 md:grid-cols-2 md:grid-rows-1">
        <section aria-label="Query editor" className="min-h-0 min-w-0 border-b md:border-r md:border-b-0">
          <QueryEditor dark={dark} />
        </section>
        <section aria-label="Analysis results" className="flex min-h-0 min-w-0 flex-col">
          <ResultsPanel dark={dark} />
        </section>
      </main>

      <SchemaDrawer open={panel === "schema"} onOpenChange={(o) => setPanel(o ? "schema" : null)} />
      <HistoryDrawer open={panel === "history"} onOpenChange={(o) => setPanel(o ? "history" : null)} />
    </div>
  );
}
