import { create } from "zustand";
import { persist } from "zustand/middleware";
import type { AnalyzeResponse, CompactSchemaMetadataDTO, Dialect, QueryType } from "./types";

export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080";
const MAX_HISTORY = 20;

export interface HistoryEntry {
  id: string;
  createdAt: number;
  queryType: QueryType;
  dialect: Dialect;
  query: string;
  response: AnalyzeResponse;
}

export interface SchemaInfo {
  fileName: string;
  ms: number;
  statements: number;
  skipped: number;
}

interface State {
  queryType: QueryType;
  dialect: Dialect;
  query: string;
  schema: CompactSchemaMetadataDTO | null;
  schemaInfo: SchemaInfo | null;
  response: AnalyzeResponse | null;
  /** Query text the current response was computed for; markers are only valid while it matches. */
  analyzedQuery: string | null;
  loading: boolean;
  error: string | null;
  history: HistoryEntry[];
  set: (patch: Partial<Pick<State, "queryType" | "dialect" | "query">>) => void;
  setSchema: (schema: CompactSchemaMetadataDTO | null, info: SchemaInfo | null) => void;
  analyze: () => Promise<void>;
  restore: (entry: HistoryEntry) => void;
  removeHistory: (id: string) => void;
  clearHistory: () => void;
}

export const useStore = create<State>()(
  persist(
    (set, get) => ({
      queryType: "SQL",
      dialect: "SQLSERVER",
      query: "",
      schema: null,
      schemaInfo: null,
      response: null,
      analyzedQuery: null,
      loading: false,
      error: null,
      history: [],

      set: (patch) => set(patch),
      setSchema: (schema, schemaInfo) => set({ schema, schemaInfo }),

      analyze: async () => {
        const { queryType, dialect, query, schema, loading } = get();
        if (loading || !query.trim()) return;
        set({ loading: true, error: null });
        try {
          const res = await fetch(`${API_URL}/api/analyze`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ queryType, dialect, query, schema: relevantSchema(schema, query) }),
          });
          if (!res.ok) {
            const body = await res.json().catch(() => null);
            throw new Error(
              res.status === 429 ? "Rate limit reached (60 requests per minute). Try again shortly."
                : res.status === 413 ? "Request too large. The query plus schema metadata exceeds 2 MB."
                : body?.error ?? `Analysis failed (HTTP ${res.status}).`,
            );
          }
          const response: AnalyzeResponse = await res.json();
          const entry: HistoryEntry = { id: crypto.randomUUID(), createdAt: Date.now(), queryType, dialect, query, response };
          set((s) => ({
            response,
            analyzedQuery: query,
            history: response.errors.length ? s.history : [entry, ...s.history].slice(0, MAX_HISTORY),
          }));
        } catch (err) {
          const message = err instanceof TypeError ? `Cannot reach the analysis API at ${API_URL}.` : (err as Error).message;
          set({ error: message });
        } finally {
          set({ loading: false });
        }
      },

      restore: (e) => set({ queryType: e.queryType, dialect: e.dialect, query: e.query, response: e.response, analyzedQuery: e.query, error: null }),
      removeHistory: (id) => set((s) => ({ history: s.history.filter((h) => h.id !== id) })),
      clearHistory: () => set({ history: [] }),
    }),
    {
      name: "query-index-advisor",
      // The schema stays in memory only (PRD FR-02); it is never written to storage.
      partialize: (s) => ({ queryType: s.queryType, dialect: s.dialect, query: s.query, history: s.history }),
    },
  ),
);

/** Only tables the query mentions are sent, keeping the payload small and exposing as little schema as possible. */
export function relevantSchema(schema: CompactSchemaMetadataDTO | null, query: string): CompactSchemaMetadataDTO | null {
  if (!schema) return null;
  const words = new Set(query.toLowerCase().match(/[a-z_][\w$#]*/g) ?? []);
  // LINQ refers to tables by entity name too (o.Customer, Set<Order>()), so match the singular as well.
  const singular = (n: string) => (n.endsWith("ies") ? n.slice(0, -3) + "y" : n.endsWith("s") ? n.slice(0, -1) : n);
  const tables = Object.fromEntries(Object.entries(schema.tables).filter(([, t]) => {
    const name = t.tableName.toLowerCase();
    return words.has(name) || words.has(singular(name));
  }));
  return { ...schema, tables };
}
