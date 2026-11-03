import pkg from "node-sql-parser";
import { fastAst } from "./fast-ddl.ts";
import type { CompactSchemaMetadataDTO, CompactTableSchema, Dialect, ExistingIndexMetadata } from "./types";

const DATABASE: Record<Dialect, string> = { SQLSERVER: "TransactSQL", POSTGRESQL: "PostgresQL", MYSQL: "MySQL" };
const DDL = /^\s*(create\s+(unique\s+)?((non)?clustered\s+)?(table|index)|alter\s+table)\b/i;
const DIALECTS: Dialect[] = ["SQLSERVER", "POSTGRESQL", "MYSQL"];

export interface SchemaParseResult {
  schema: CompactSchemaMetadataDTO;
  statements: number;
  skipped: number;
}

/** `fast: false` forces node-sql-parser for every statement (used to check both paths agree). */
export function parseSchemaFile(text: string, fileName: string, dialect: Dialect, { fast = true } = {}): SchemaParseResult {
  if (fileName.toLowerCase().endsWith(".json")) return { schema: fromJson(text, dialect), statements: 0, skipped: 0 };
  return fromDdl(text, dialect, fast);
}

function fromJson(text: string, dialect: Dialect): CompactSchemaMetadataDTO {
  const raw = JSON.parse(text);
  if (!raw || typeof raw.tables !== "object") throw new Error('JSON schema must have a "tables" object (see CompactSchemaMetadataDTO).');
  const tables: Record<string, CompactTableSchema> = {};
  for (const [key, t] of Object.entries<Partial<CompactTableSchema>>(raw.tables)) {
    if (!t || typeof t !== "object") throw new Error(`Table "${key}" is not an object.`);
    tables[key] = { tableName: t.tableName ?? key, columns: t.columns ?? {}, indexes: Array.isArray(t.indexes) ? t.indexes : [] };
  }
  return { dialect: DIALECTS.includes(raw.dialect) ? raw.dialect : dialect, tables };
}

function fromDdl(text: string, dialect: Dialect, fast: boolean): SchemaParseResult {
  let parser: InstanceType<typeof pkg.Parser> | null = null;
  const tables: Tables = { byKey: {}, byName: new Map() };
  let statements = 0;
  let skipped = 0;
  for (const stmt of splitStatements(text)) {
    if (!DDL.test(stmt)) continue; // SET, GO, procedures, grants... carry no schema
    statements++;
    let ast = fast ? fastAst(stmt) : null;
    try {
      // node-sql-parser's T-SQL grammar rejects UNIQUE [NON]CLUSTERED; uniqueness is read from `stmt` anyway.
      ast ??= (parser ??= new pkg.Parser()).astify(stmt.replace(/^(\s*create\s+unique)\s+(non)?clustered\b/i, "$1"), { database: DATABASE[dialect] });
    } catch {
      skipped++;
      continue;
    }
    for (const node of Array.isArray(ast) ? ast : [ast]) apply(node, stmt, tables);
  }
  return { schema: { dialect, tables: tables.byKey }, statements, skipped };
}

// ponytail: comments are stripped and statements split on ';' / GO without tracking string literals;
// a ';' inside a DEFAULT string splits that one statement, which is then just skipped.
export function splitStatements(text: string): string[] {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    .replace(/--[^\n]*/g, "")
    .split(/^\s*GO\s*;?\s*$|;/im)
    .map((s) => s.trim())
    .filter(Boolean);
}

/* eslint-disable @typescript-eslint/no-explicit-any -- node-sql-parser ASTs are untyped per dialect */
function apply(node: any, stmt: string, tables: Tables) {
  if (node?.type === "create" && node.keyword === "table") {
    const t = ensure(tables, node.table?.[0]);
    for (const def of node.create_definitions ?? []) define(t, def);
  } else if (node?.type === "create" && node.keyword === "index") {
    const t = ensure(tables, node.table);
    t.indexes.push({
      indexName: node.index,
      isUnique: /^\s*create\s+unique/i.test(stmt),
      keyColumns: (node.index_columns ?? []).map(ident),
      includeColumns: (node.include?.columns ?? []).map(ident),
    });
  } else if (node?.type === "alter") { // MySQL's AST omits keyword: "table"; DDL regex guarantees ALTER TABLE
    const t = ensure(tables, node.table?.[0]);
    // ADD CONSTRAINT nests its definition; ADD <column> is the definition itself.
    for (const e of node.expr ?? []) if (e.action === "add") [e.create_definitions ?? e].flat().forEach((d) => define(t, d));
  }
}

function define(t: CompactTableSchema, def: any) {
  if (!def) return;
  if (def.resource === "column") {
    const name = ident(def.column);
    const isPrimaryKey = !!def.primary_key;
    t.columns[name] = { name, dataType: dataType(def.definition), isNullable: !isPrimaryKey && def.nullable?.type !== "not null", isPrimaryKey };
    if (isPrimaryKey) addIndex(t, { indexName: `PK_${t.tableName}`, isUnique: true, keyColumns: [name], includeColumns: [] });
  } else if (def.resource === "constraint" && /primary key/i.test(def.constraint_type ?? "")) {
    const keys = (def.definition ?? []).map(ident);
    for (const k of keys) if (t.columns[k]) Object.assign(t.columns[k], { isPrimaryKey: true, isNullable: false });
    addIndex(t, { indexName: def.constraint ?? `PK_${t.tableName}`, isUnique: true, keyColumns: keys, includeColumns: [] });
  } else if ((def.resource === "constraint" && /unique/i.test(def.constraint_type ?? "")) || def.resource === "index") {
    const keys = (def.definition ?? []).map(ident);
    addIndex(t, { indexName: def.constraint ?? def.index ?? `UQ_${t.tableName}_${keys.join("_")}`, isUnique: def.resource !== "index", keyColumns: keys, includeColumns: [] });
  }
}

function addIndex(t: CompactTableSchema, index: ExistingIndexMetadata) {
  if (!t.indexes.some((i) => i.indexName === index.indexName)) t.indexes.push(index);
}

// Tables keyed "db.table" (or "table"); byName lets `CREATE INDEX ... ON Orders` find "dbo.Orders" in O(1).
interface Tables {
  byKey: Record<string, CompactTableSchema>;
  byName: Map<string, CompactTableSchema>;
}

function ensure(tables: Tables, ref: any): CompactTableSchema {
  const name: string = ref?.table ?? "unknown";
  const key = ref?.db ? `${ref.db}.${name}` : name;
  const existing = tables.byKey[key] ?? tables.byName.get(name.toLowerCase());
  if (existing) return existing;
  const t = (tables.byKey[key] = { tableName: name, columns: {}, indexes: [] });
  tables.byName.set(name.toLowerCase(), t);
  return t;
}

// Column names come back as a string, { value }, { expr: { value } } or a column_ref, depending on dialect.
function ident(x: any): string {
  if (typeof x === "string") return x;
  if (!x) return "";
  if (x.type === "column_ref") return ident(x.column);
  return ident(x.expr ?? x.value);
}

function dataType(d: any): string {
  if (!d?.dataType) return "unknown";
  const t = String(d.dataType).toLowerCase();
  if (d.length == null) return t;
  return `${t}(${d.length}${d.scale != null ? `,${d.scale}` : ""})`;
}
