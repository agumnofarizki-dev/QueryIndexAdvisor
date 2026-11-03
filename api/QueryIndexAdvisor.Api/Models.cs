namespace QueryIndexAdvisor.Api;

// Mirrors the client-side CompactSchemaMetadataDTO (PRD §6.A).
public record ColumnMetadata(string Name, string DataType, bool IsNullable, bool IsPrimaryKey);

public record ExistingIndexMetadata(string IndexName, bool IsUnique, List<string> KeyColumns, List<string> IncludeColumns);

public record CompactTableSchema(string TableName, Dictionary<string, ColumnMetadata> Columns, List<ExistingIndexMetadata> Indexes);

public record CompactSchemaMetadataDTO(string Dialect, Dictionary<string, CompactTableSchema> Tables);

public record AnalyzeRequest(string QueryType, string Dialect, string Query, CompactSchemaMetadataDTO? Schema);

/// <param name="EndColumn">Exclusive end on the same line, when the span fits on one line.</param>
public record Diagnostic(string Code, string Message, int Line, int Column, int? EndColumn = null);

public record KeyColumn(string Name, bool Descending = false);

public record CoveringIndex(string IndexName, List<string> KeyColumns, List<string> IncludeColumns, bool FullyCovered);

public record IndexRecommendation(
    string Table,
    List<string> EqualityColumns,
    List<string> RangeColumns,
    List<KeyColumn> SortColumns,
    List<string> IncludeColumns,
    List<KeyColumn> KeyColumns,
    string IndexName,
    string Ddl,
    CoveringIndex? CoveredBy);

public record AnalyzeResponse(
    List<Diagnostic> Errors,
    List<Diagnostic> Warnings,
    List<IndexRecommendation> Recommendations,
    long ExecutionTimeMs);
