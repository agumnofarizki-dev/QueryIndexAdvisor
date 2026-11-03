"use client";

import Editor from "@monaco-editor/react";
import { AlertTriangle, Check, CheckCircle2, Copy, Download, Info, Loader2, XCircle } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { revealInEditor } from "@/components/QueryEditor";
import { useStore } from "@/lib/store";
import type { Diagnostic, Dialect, IndexRecommendation } from "@/lib/types";

const LINQ_EXAMPLE = `var orders = await db.Orders
    .Where(o => o.Customer.Country == "ID"
             && o.Status == "Paid"
             && o.OrderDate >= from
             && o.ShippedAt.Value.Year == 2024)
    .OrderByDescending(o => o.OrderDate)
    .Select(o => new { o.Id, o.Total, o.Customer.Name })
    .ToListAsync();`;

const SQL_EXAMPLES: Record<Dialect, string> = {
  SQLSERVER: `SELECT o.Id, o.Total
FROM dbo.Orders o
JOIN dbo.Customers c ON c.Id = o.CustomerId
WHERE c.Country = 'ID'
  AND o.Status = 'Paid'
  AND o.OrderDate >= @from
  AND YEAR(o.ShippedAt) = 2024
ORDER BY o.OrderDate DESC;`,
  POSTGRESQL: `SELECT o.id, o.total
FROM public.orders o
JOIN public.customers c ON c.id = o.customer_id
WHERE c.country = 'ID'
  AND o.status = 'paid'
  AND o.order_date >= $1
  AND o.shipped_at::date = $2
ORDER BY o.order_date DESC
LIMIT 50;`,
  MYSQL: `SELECT o.id, o.total
FROM \`shop\`.\`orders\` o
JOIN \`shop\`.\`customers\` c ON c.id = o.customer_id
WHERE c.country = 'ID'
  AND o.status = 'paid'
  AND o.created_at >= ?
  AND DATE(o.shipped_at) = ?
ORDER BY o.created_at DESC
LIMIT 50;`,
};

export function ResultsPanel({ dark }: { dark: boolean }) {
  const { response, loading, error, dialect, queryType, set } = useStore();

  if (loading) return <Centered><Loader2 className="size-5 animate-spin" /> Analyzing…</Centered>;

  const banner = error && (
    <div role="alert" className="m-4 flex items-start gap-2 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">
      <XCircle className="mt-0.5 size-4 shrink-0" /> {error}
    </div>
  );

  if (!response)
    return (
      <div className="flex h-full flex-col">
        {banner}
        <Centered>
          <p>Paste a query into the editor, then press <Kbd>Ctrl</Kbd> + <Kbd>Enter</Kbd>.</p>
          <Button variant="outline" size="sm" onClick={() => set({ query: queryType === "LINQ" ? LINQ_EXAMPLE : SQL_EXAMPLES[dialect] })}>
            Load {queryType === "LINQ" ? "LINQ" : "SQL"} example
          </Button>
        </Centered>
      </div>
    );

  const newIndexes = response.recommendations.filter((r) => !r.coveredBy?.fullyCovered);
  const script = buildScript(response.recommendations, dialect);

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      {banner}
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-3">
        <p className="mr-auto text-sm text-muted-foreground">
          <span className="font-medium text-foreground">{newIndexes.length}</span> index change{newIndexes.length === 1 ? "" : "s"} · {response.recommendations.length - newIndexes.length} already covered · {response.executionTimeMs} ms
        </p>
        <CopyButton text={script} label="Copy DDL" disabled={!response.recommendations.length} />
        <Button variant="outline" size="sm" disabled={!response.recommendations.length} onClick={() => download(script)}>
          <Download /> Export .sql
        </Button>
      </div>

      <div className="space-y-4 p-4">
        <DiagnosticList title="Parse errors" items={response.errors} tone="error" />
        <DiagnosticList title="Anti-patterns" items={response.warnings} tone="warning" />

        {!response.errors.length && !response.recommendations.length && (
          <p className="flex items-center gap-2 text-sm text-muted-foreground"><Info className="size-4" /> No seekable predicates found, so no index is recommended.</p>
        )}

        {response.recommendations.map((r) => <RecommendationCard key={r.table + r.indexName} rec={r} dialect={dialect} />)}

        {response.recommendations.length > 0 && (
          <section aria-label="Generated DDL" className="overflow-hidden rounded-lg border">
            <h3 className="border-b bg-muted/50 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Generated DDL</h3>
            <Editor
              height={`${Math.min(script.split("\n").length, 30) * 19 + 16}px`}
              language="sql"
              theme={dark ? "vs-dark" : "light"}
              value={script}
              options={{ readOnly: true, minimap: { enabled: false }, scrollBeyondLastLine: false, lineNumbers: "off", fontSize: 13, padding: { top: 8 }, automaticLayout: true }}
            />
          </section>
        )}
      </div>
    </div>
  );
}

function RecommendationCard({ rec, dialect }: { rec: IndexRecommendation; dialect: Dialect }) {
  const cover = rec.coveredBy;
  return (
    <article className="rounded-lg border bg-card p-4">
      <header className="mb-3 flex flex-wrap items-start gap-2">
        <div className="mr-auto min-w-0">
          <h3 className="font-medium">{rec.table}</h3>
          <p className="truncate font-mono text-xs text-muted-foreground" title={rec.indexName}>{rec.indexName}</p>
        </div>
        <CopyButton text={rec.ddl} label="Copy" />
      </header>

      {cover?.fullyCovered && (
        <Notice tone="ok" icon={<CheckCircle2 className="size-4" />}>
          <strong>Index Already Covered</strong> by <code>{cover.indexName}</code> ({cover.keyColumns.join(", ")}
          {cover.includeColumns.length > 0 && <> INCLUDE {cover.includeColumns.join(", ")}</>}). No new index needed.
        </Notice>
      )}
      {cover && !cover.fullyCovered && (
        <Notice tone="warn" icon={<AlertTriangle className="size-4" />}>
          <code>{cover.indexName}</code> ({cover.keyColumns.join(", ")}) already has this key but is missing some columns. The DDL widens that index instead of adding an overlapping one.
        </Notice>
      )}

      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
        <Row label="Equality" cols={rec.equalityColumns} />
        <Row label="Range" cols={rec.rangeColumns} />
        <Row label="Sort" cols={rec.sortColumns.map((s) => s.name + (s.descending ? " DESC" : ""))} />
        <Row label="Key order" cols={rec.keyColumns.map((k) => k.name + (k.descending ? " DESC" : ""))} strong />
        {/* MySQL has no INCLUDE; the DDL appends these to the key instead. */}
        <Row label={dialect === "MYSQL" ? "Payload" : "Include"} cols={rec.includeColumns} />
      </dl>
    </article>
  );
}

function Row({ label, cols, strong }: { label: string; cols: string[]; strong?: boolean }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="flex flex-wrap gap-1">
        {cols.length === 0 ? <span className="text-muted-foreground">-</span> : cols.map((c, i) => (
          <span key={c + i} className={`rounded px-1.5 py-0.5 font-mono text-xs ${strong ? "bg-primary text-primary-foreground" : "bg-muted"}`}>
            {strong && <span className="mr-1 opacity-60">{i + 1}</span>}{c}
          </span>
        ))}
      </dd>
    </>
  );
}

function DiagnosticList({ title, items, tone }: { title: string; items: Diagnostic[]; tone: "error" | "warning" }) {
  if (!items.length) return null;
  const color = tone === "error" ? "text-destructive" : "text-amber-600 dark:text-amber-400";
  const Icon = tone === "error" ? XCircle : AlertTriangle;
  return (
    <section>
      <h3 className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">{title}</h3>
      <ul className="space-y-1">
        {items.map((d, i) => (
          <li key={i}>
            <button type="button" onClick={() => revealInEditor(d.line, d.column)} className="flex w-full items-start gap-2 rounded-md p-2 text-left text-sm hover:bg-muted">
              <Icon className={`mt-0.5 size-4 shrink-0 ${color}`} />
              <span><span className="font-mono text-xs text-muted-foreground">Ln {d.line}, Col {d.column}</span> {d.message}</span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}

function Notice({ tone, icon, children }: { tone: "ok" | "warn"; icon: React.ReactNode; children: React.ReactNode }) {
  const style = tone === "ok"
    ? "border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
    : "border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-300";
  return <div className={`mb-3 flex items-start gap-2 rounded-md border p-2 text-sm ${style}`}><span className="mt-0.5 shrink-0">{icon}</span><p>{children}</p></div>;
}

function CopyButton({ text, label, disabled }: { text: string; label: string; disabled?: boolean }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard blocked (e.g. insecure context); the DDL stays selectable in the editor */ }
  };
  return (
    <Button variant="outline" size="sm" onClick={copy} disabled={disabled}>
      {copied ? <Check /> : <Copy />} {copied ? "Copied" : label}
    </Button>
  );
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center text-sm text-muted-foreground">{children}</div>;
}

function Kbd({ children }: { children: React.ReactNode }) {
  return <kbd className="rounded border bg-muted px-1.5 py-0.5 font-mono text-xs">{children}</kbd>;
}

function buildScript(recs: IndexRecommendation[], dialect: string) {
  const header = `-- Generated by QueryIndex Advisor (${dialect}). Review before running in production.\n`;
  return header + recs.map((r) => `\n-- ${r.table}: key (${r.keyColumns.map((k) => k.name).join(", ")})\n${r.ddl}\n`).join("");
}

function download(script: string) {
  const url = URL.createObjectURL(new Blob([script], { type: "application/sql" }));
  const a = Object.assign(document.createElement("a"), { href: url, download: `index-recommendations-${new Date().toISOString().slice(0, 16).replace(/[:T]/g, "")}.sql` });
  a.click();
  URL.revokeObjectURL(url);
}
