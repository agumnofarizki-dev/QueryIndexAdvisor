// Client-side in-memory schema (PRD §6.A). Also the accepted `.json` schema file format.
export interface ColumnMetadata {
  name: string;
  dataType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
}

export interface ExistingIndexMetadata {
  indexName: string;
  isUnique: boolean;
  keyColumns: string[];
  includeColumns: string[];
}

export interface CompactTableSchema {
  tableName: string;
  columns: Record<string, ColumnMetadata>;
  indexes: ExistingIndexMetadata[];
}

export type Dialect = "SQLSERVER" | "POSTGRESQL" | "MYSQL";
export type QueryType = "SQL" | "LINQ";

export interface CompactSchemaMetadataDTO {
  dialect: Dialect;
  tables: Record<string, CompactTableSchema>;
}

// Mirrors api/QueryIndexAdvisor.Api/Models.cs
export interface Diagnostic {
  code: string;
  message: string;
  line: number;
  column: number;
  endColumn?: number | null;
}

export interface KeyColumn {
  name: string;
  descending: boolean;
}

export interface CoveringIndex {
  indexName: string;
  keyColumns: string[];
  includeColumns: string[];
  fullyCovered: boolean;
}

export interface IndexRecommendation {
  table: string;
  equalityColumns: string[];
  rangeColumns: string[];
  sortColumns: KeyColumn[];
  includeColumns: string[];
  keyColumns: KeyColumn[];
  indexName: string;
  ddl: string;
  coveredBy: CoveringIndex | null;
}

export interface AnalyzeResponse {
  errors: Diagnostic[];
  warnings: Diagnostic[];
  recommendations: IndexRecommendation[];
  executionTimeMs: number;
}
