/**
 * Hand-written scanner for the DDL shapes schema dumps are made of: CREATE TABLE, CREATE INDEX and
 * ALTER TABLE ... ADD CONSTRAINT. ~100x faster than node-sql-parser (PRD NFR-01: 5 MB in < 300 ms).
 * It emits the same node shapes node-sql-parser does, and returns null for anything it is not sure
 * about so the caller falls back to the full parser.
 */

/* eslint-disable @typescript-eslint/no-explicit-any -- mirrors node-sql-parser's untyped AST */
export function fastAst(stmt: string): any | null {
  if (/^\s*create\b/i.test(stmt)) return /^\s*create\s+[\w\s]*?\bindex\b/i.test(stmt) ? createIndex(stmt) : createTable(stmt);
  if (/^\s*alter\s+table\b/i.test(stmt)) return alterTable(stmt);
  return null;
}

function createTable(s: string) {
  let i = match(s, 0, /\s*create\s+(?:(?:global\s+|local\s+)?(?:temporary|temp)\s+)?table\s+(?:if\s+not\s+exists\s+)?/iy);
  const name = i < 0 ? null : qualified(s, i);
  if (!name) return null;
  i = skipWs(s, name.end);
  if (s[i] !== "(") return null; // CREATE TABLE ... AS SELECT, PARTITION OF, etc.
  const body = block(s, i);
  if (!body) return null;
  const defs = [];
  for (const part of splitTop(body.inner)) {
    const def = tableItem(part) ?? column(part);
    if (!def) return null;
    defs.push(def);
  }
  return { type: "create", keyword: "table", table: [name.ref], create_definitions: defs };
}

function createIndex(s: string) {
  let i = match(s, 0, /\s*create\s+(?:unique\s+)?(?:(?:non)?clustered\s+)?index\s+(?:concurrently\s+)?(?:if\s+not\s+exists\s+)?/iy);
  const name = i < 0 ? null : ident(s, i);
  if (!name) return null;
  i = match(s, name.end, /\s*on\s+(?:only\s+)?/iy);
  const table = i < 0 ? null : qualified(s, i);
  if (!table) return null;
  i = Math.max(match(s, table.end, /\s*using\s+\w+/iy), table.end);
  i = skipWs(s, i);
  const keys = s[i] === "(" ? block(s, i) : null;
  const keyColumns = keys && columnList(keys.inner);
  if (!keyColumns) return null;
  let includeColumns: string[] = [];
  const inc = match(s, keys.end, /\s*include\s*/iy);
  if (inc >= 0) {
    const b = s[inc] === "(" ? block(s, inc) : null;
    const cols = b && columnList(b.inner);
    if (!cols) return null;
    includeColumns = cols;
  }
  return { type: "create", keyword: "index", index: name.value, table: table.ref, index_columns: keyColumns, include: { columns: includeColumns } };
}

function alterTable(s: string) {
  const i = match(s, 0, /\s*alter\s+table\s+(?:only\s+)?(?:if\s+exists\s+)?/iy);
  const table = i < 0 ? null : qualified(s, i);
  if (!table) return null;
  const add = match(s, table.end, /\s*(?:with\s+(?:no)?check\s+)?add\s+/iy);
  // CHECK CONSTRAINT, ALTER COLUMN, DROP ... don't change keys or indexes we track.
  if (add < 0) return { type: "noop" };
  const defs = [];
  for (const part of splitTop(s.slice(add))) {
    const def = tableItem(part);
    if (!def) return null; // ADD <column> etc.: let the full parser decide
    defs.push(def);
  }
  return { type: "alter", keyword: "table", table: [table.ref], expr: defs.map((d) => ({ action: "add", create_definitions: d })) };
}

/** Table-level constraint / index definition, or null when `part` is a column. */
function tableItem(part: string): any | null {
  let i = 0;
  let constraint: string | null = null;
  const c = match(part, 0, /\s*constraint\s+/iy);
  if (c >= 0) {
    const n = ident(part, c);
    if (!n) return null;
    constraint = n.value;
    i = n.end;
  }
  const pk = match(part, i, /\s*primary\s+key\s*(?:(?:non)?clustered\s*)?/iy);
  if (pk >= 0) {
    const cols = parenColumns(part, pk);
    return cols && { resource: "constraint", constraint_type: "primary key", constraint, definition: cols };
  }
  const uq = match(part, i, /\s*unique\b\s*(?:(?:key|index)\s+)?(?:(?:non)?clustered\s*)?/iy);
  if (uq >= 0) {
    const named = part[skipWs(part, uq)] === "(" ? null : ident(part, uq);
    const cols = parenColumns(part, named ? match(part, named.end, /\s*(?:(?:non)?clustered\s*)?/iy) : uq);
    return cols && { resource: "constraint", constraint_type: "unique", constraint: constraint ?? named?.value ?? null, definition: cols };
  }
  if (match(part, i, /\s*(?:foreign\s+key|check|default|fulltext|spatial|exclude|period\s+for|like)\b/iy) >= 0) return { resource: "skip" };
  if (constraint) return null; // unknown constraint kind
  const ix = match(part, 0, /\s*(?:key|index)\s+/iy);
  if (ix >= 0) {
    const n = ident(part, ix);
    const cols = n && parenColumns(part, match(part, n.end, /\s*(?:(?:non)?clustered\s*)?(?:using\s+\w+\s*)?/iy));
    return cols && { resource: "index", index: n.value, definition: cols };
  }
  return null;
}

// Words that end a column's data type. Multi-word types (character varying, double precision,
// timestamp with time zone) keep going because their later words are not in this set.
const COLUMN_OPTIONS = new Set(["not", "null", "primary", "identity", "default", "constraint", "collate", "unique", "references", "check", "auto_increment", "generated", "comment", "on", "character", "charset", "sparse", "rowguidcol", "filestream", "masked", "encrypted"]);

function column(part: string): any | null {
  const n = ident(part, 0);
  if (!n) return null;
  const start = skipWs(part, n.end);
  if (start >= part.length) return null;

  // The type runs until a top-level space followed by a column-option keyword.
  let end = part.length;
  for (let j = start + 1, depth = 0; j < part.length; j++) {
    const c = part.charCodeAt(j);
    if (c === 40) depth++; // (
    else if (c === 41) depth--; // )
    else if (depth === 0 && c <= 32) {
      const w = skipWs(part, j);
      let k = w;
      while (k < part.length && isWordChar(part.charCodeAt(k))) k++;
      if (COLUMN_OPTIONS.has(part.slice(w, k).toLowerCase())) { end = j; break; }
      j = w - 1;
    }
  }
  let dataType = part.slice(start, end);
  const tail = part.slice(end);
  if (COMPUTED.test(dataType)) dataType = "computed";
  else {
    if (dataType.charCodeAt(0) === 91 /* [ */ || dataType.includes('"') || dataType.includes("`")) dataType = dataType.replace(/[[\]"`]/g, "");
    if (/\s/.test(dataType)) dataType = dataType.replace(/\s*([(),])\s*/g, "$1").replace(/\s+/g, " ");
  }
  return {
    resource: "column",
    column: n.value,
    definition: { dataType },
    nullable: NOT_NULL.test(tail) ? { type: "not null" } : null,
    primary_key: PRIMARY_KEY.test(tail) ? "primary key" : null,
  };
}

const COMPUTED = /^as\b/i;
const NOT_NULL = /\bnot\s+null\b/i;
const PRIMARY_KEY = /\bprimary\s+key\b/i;

function isWordChar(c: number) {
  return (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c === 95;
}

function parenColumns(s: string, i: number): string[] | null {
  if (i < 0) return null;
  i = skipWs(s, i);
  const b = s[i] === "(" ? block(s, i) : null;
  return b && columnList(b.inner);
}

/** Plain column names with optional ASC/DESC/NULLS; null for expressions, prefixes or opclasses. */
function columnList(inner: string): string[] | null {
  const cols = [];
  for (const part of splitTop(inner)) {
    const n = ident(part, 0);
    if (!n || !/^\s*(?:asc|desc)?\s*(?:nulls\s+(?:first|last))?\s*$/i.test(part.slice(n.end))) return null;
    cols.push(n.value);
  }
  return cols.length ? cols : null;
}

function qualified(s: string, i: number) {
  const parts: string[] = [];
  for (;;) {
    const n = ident(s, i);
    if (!n) return null;
    parts.push(n.value);
    i = skipWs(s, n.end);
    if (s[i] !== ".") break;
    i++;
  }
  return { ref: { db: parts.length > 1 ? parts[parts.length - 2] : null, table: parts[parts.length - 1] }, end: i };
}

const WORD = /[\p{L}\p{N}_$#@]+/uy;

function ident(s: string, i: number): { value: string; end: number } | null {
  i = skipWs(s, i);
  const q = s[i];
  if (q === "[") {
    let out = "";
    for (let from = i + 1; ;) {
      const close = s.indexOf("]", from);
      if (close < 0) return null;
      out += s.slice(from, close);
      if (s[close + 1] !== "]") return out ? { value: out, end: close + 1 } : null;
      out += "]"; // "]]" escapes a literal ]
      from = close + 2;
    }
  }
  if (q === '"' || q === "`") {
    const j = s.indexOf(q, i + 1);
    return j > i + 1 ? { value: s.slice(i + 1, j), end: j + 1 } : null;
  }
  WORD.lastIndex = i;
  const m = WORD.exec(s);
  return m ? { value: m[0], end: WORD.lastIndex } : null;
}

/** Content of the parenthesized block starting at s[i] === "(", skipping quoted text. */
function block(s: string, i: number): { inner: string; end: number } | null {
  let depth = 0;
  for (let j = i; j < s.length; j++) {
    const ch = s[j];
    if (ch === "'" || ch === '"' || ch === "`" || ch === "[") {
      const close = s.indexOf(ch === "[" ? "]" : ch, j + 1);
      if (close < 0) return null;
      j = close;
    } else if (ch === "(") depth++;
    else if (ch === ")" && --depth === 0) return { inner: s.slice(i + 1, j), end: j + 1 };
  }
  return null;
}

/** Splits on commas that are not inside parentheses or quotes. */
function splitTop(s: string): string[] {
  const parts = [];
  let depth = 0;
  let start = 0;
  for (let j = 0; j < s.length; j++) {
    const ch = s[j];
    if (ch === "'" || ch === '"' || ch === "`" || ch === "[") {
      const close = s.indexOf(ch === "[" ? "]" : ch, j + 1);
      if (close < 0) break;
      j = close;
    } else if (ch === "(") depth++;
    else if (ch === ")") depth--;
    else if (ch === "," && depth === 0) {
      parts.push(s.slice(start, j));
      start = j + 1;
    }
  }
  parts.push(s.slice(start));
  return parts.map((p) => p.trim());
}

function skipWs(s: string, i: number) {
  while (i < s.length && s.charCodeAt(i) <= 32) i++;
  return i;
}

/** Index after a sticky-regex match at i, or -1. */
function match(s: string, i: number, re: RegExp) {
  re.lastIndex = i;
  return re.test(s) ? re.lastIndex : -1;
}
