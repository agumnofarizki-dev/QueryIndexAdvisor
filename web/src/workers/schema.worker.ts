import { parseSchemaFile, type SchemaParseResult } from "@/lib/schema-parser";
import type { Dialect } from "@/lib/types";

export type SchemaWorkerResponse = ({ ok: true; ms: number } & SchemaParseResult) | { ok: false; error: string };

// The file is read and parsed here, off the main thread; its content never leaves the browser.
self.onmessage = async (e: MessageEvent<{ file: File; dialect: Dialect }>) => {
  const start = performance.now();
  try {
    const result = parseSchemaFile(await e.data.file.text(), e.data.file.name, e.data.dialect);
    postMessage({ ok: true, ms: performance.now() - start, ...result } satisfies SchemaWorkerResponse);
  } catch (err) {
    postMessage({ ok: false, error: err instanceof Error ? err.message : String(err) } satisfies SchemaWorkerResponse);
  }
};
