/**
 * Real-time LINQ anti-pattern hints (PRD FR-04), run in the browser on every pause in typing so
 * they cost no API quota. Pattern matching only; the backend's Roslyn analysis is authoritative.
 */
export interface InspectorHint {
  code: string;
  message: string;
  line: number;
  column: number;
  endColumn: number;
}

const WRAPPING_METHODS = "ToLower|ToUpper|ToLowerInvariant|ToUpperInvariant|Trim|TrimStart|TrimEnd|ToString";
const DATE_PARTS = "Year|Month|Day|Hour|Minute|Second|Date|DayOfWeek|DayOfYear";

export function inspectLinq(code: string): InspectorHint[] {
  const hints: InspectorHint[] = [];
  const add = (hintCode: string, index: number, length: number, message: string) => {
    const before = code.slice(0, index);
    const column = index - before.lastIndexOf("\n");
    hints.push({ code: hintCode, message, line: before.split("\n").length, column, endColumn: column + length });
  };

  for (const m of code.matchAll(/\.(AsEnumerable|AsAsyncEnumerable)\s*\(\s*\)/g))
    add("CLIENT_EVAL", m.index + 1, m[0].length - 1, `${m[1]}() switches to in-memory evaluation; operators after it are not translated to SQL.`);

  // Only members of lambda parameters / range variables are columns; `request.Email.ToLower()` is not.
  const params = new Set<string>();
  for (const m of code.matchAll(/\b(\w+)\s*=>/g)) params.add(m[1]);
  for (const m of code.matchAll(/\(([\w\s,]+)\)\s*=>/g)) for (const p of m[1].split(",")) params.add(p.trim().split(/\s+/).pop()!);
  for (const m of code.matchAll(/\b(?:from|join)\s+(\w+)\s+in\b/g)) params.add(m[1]);
  params.delete("");
  if (params.size > 0) {
    const member = (rest: string) => new RegExp(`\\b(?:${[...params].join("|")})\\.(\\w+)${rest}`, "g");

    for (const m of code.matchAll(member(`(?:\\.(\\w+))?\\.(${WRAPPING_METHODS})\\s*\\(\\s*\\)`)))
      add("NON_SARGABLE", m.index, m[0].length, `.${m[3]}() on '${m[2] ?? m[1]}' becomes a SQL function, so an index on it cannot be seeked. Compare the raw column (a case-insensitive collation handles casing).`);
    for (const m of code.matchAll(member(`(?:\\.Value)?\\.(${DATE_PARTS})\\b(?!\\s*\\()`)))
      add("NON_SARGABLE", m.index, m[0].length, `.${m[2]} on '${m[1]}' becomes DATEPART(), so an index on it cannot be seeked. Filter with a date range instead (>= start && < end).`);
    for (const m of code.matchAll(member(`\\.(Contains|EndsWith)\\s*\\(`)))
      add("LEADING_WILDCARD", m.index, m[0].length - 1, `string.${m[2]} on '${m[1]}' translates to LIKE '%...', which cannot seek an index. Use StartsWith or full-text search.`);
  }

  return hints.sort((a, b) => a.line - b.line || a.column - b.column);
}
