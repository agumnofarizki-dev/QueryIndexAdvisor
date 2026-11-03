// Run: npm test (node --test, native TypeScript stripping)
import assert from "node:assert/strict";
import test from "node:test";
import { parseSchemaFile } from "./schema-parser.ts";
import { fastAst } from "./fast-ddl.ts";

const SSMS = `SET ANSI_NULLS ON
GO
/* Object: Table [dbo].[Orders] */
CREATE TABLE [dbo].[Orders](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[CustomerId] [int] NOT NULL,
	[Total] [decimal](18, 2) NULL,
 CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id] ASC)
 WITH (PAD_INDEX = OFF, ALLOW_ROW_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] ([CustomerId] ASC) INCLUDE([Total]) WITH (ONLINE = OFF) ON [PRIMARY]
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Orders_Total] ON [dbo].[Orders] ([Total])
GO
ALTER TABLE [dbo].[Orders] WITH CHECK ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id])
GO
ALTER TABLE [dbo].[Orders] CHECK CONSTRAINT [FK_Orders_Customers]
GO
CREATE PROCEDURE dbo.Noise AS SELECT 1
GO`;
const MYSQL = "CREATE TABLE orders (id int NOT NULL AUTO_INCREMENT, customer_id int NOT NULL, PRIMARY KEY (id), KEY ix_c (customer_id), UNIQUE KEY ux_c (customer_id, id)) ENGINE=InnoDB;";
const PG = "CREATE TABLE public.orders (id serial PRIMARY KEY, total numeric(18,2)); CREATE INDEX ix_o ON public.orders USING btree (id) INCLUDE (total); ALTER TABLE public.orders ADD CONSTRAINT uq_total UNIQUE (total);";

test("SSMS-style T-SQL script", () => {
  const { schema, skipped } = parseSchemaFile(SSMS, "schema.sql", "SQLSERVER");
  assert.equal(skipped, 0);
  const orders = schema.tables["dbo.Orders"];
  assert.deepEqual(Object.keys(orders.columns), ["Id", "CustomerId", "Total"]);
  assert.deepEqual(orders.columns.Id, { name: "Id", dataType: "int", isNullable: false, isPrimaryKey: true });
  assert.equal(orders.columns.Total.dataType, "decimal(18,2)");
  assert.equal(orders.columns.Total.isNullable, true);
  assert.deepEqual(orders.indexes, [
    { indexName: "PK_Orders", isUnique: true, keyColumns: ["Id"], includeColumns: [] },
    { indexName: "IX_Orders_CustomerId", isUnique: false, keyColumns: ["CustomerId"], includeColumns: ["Total"] },
    { indexName: "UX_Orders_Total", isUnique: true, keyColumns: ["Total"], includeColumns: [] },
  ]);
});

test("MySQL inline keys and PostgreSQL INCLUDE", () => {
  const mysql = parseSchemaFile(MYSQL, "s.sql", "MYSQL").schema.tables.orders;
  assert.deepEqual(mysql.indexes.map((i) => [i.indexName, i.isUnique, i.keyColumns]), [["PK_orders", true, ["id"]], ["ix_c", false, ["customer_id"]], ["ux_c", true, ["customer_id", "id"]]]);
  assert.equal(mysql.columns.id.isPrimaryKey, true);

  const pg = parseSchemaFile(PG, "s.sql", "POSTGRESQL").schema.tables["public.orders"];
  assert.deepEqual(pg.indexes.map((i) => i.indexName), ["PK_orders", "ix_o", "uq_total"]);
  assert.deepEqual(pg.indexes[1], { indexName: "ix_o", isUnique: false, keyColumns: ["id"], includeColumns: ["total"] });
});

test("fast scanner and node-sql-parser produce the same schema", () => {
  for (const [sql, dialect] of [[SSMS, "SQLSERVER"], [MYSQL, "MYSQL"], [PG, "POSTGRESQL"]] as const) {
    const fast = parseSchemaFile(sql, "s.sql", dialect);
    const full = parseSchemaFile(sql, "s.sql", dialect, { fast: false });
    assert.deepEqual(fast.schema, full.schema, dialect);
  }
});

test("fast scanner defers what it does not understand", () => {
  assert.equal(fastAst("CREATE INDEX ix ON t (LOWER(name))"), null); // expression index
  assert.equal(fastAst("ALTER TABLE t ADD note varchar(10)"), null); // ADD column
  assert.equal(fastAst("CREATE TABLE t AS SELECT 1"), null);
  const { schema, skipped } = parseSchemaFile("CREATE TABLE t (id int); ALTER TABLE t ADD note varchar(10);", "s.sql", "MYSQL");
  assert.equal(skipped, 0);
  assert.deepEqual(Object.keys(schema.tables.t.columns), ["id", "note"]);
});

test("unparseable statements are skipped, not fatal", () => {
  const { schema, statements, skipped } = parseSchemaFile("CREATE TABLE ok (id int); CREATE TABLE broken (id int,,);", "s.sql", "SQLSERVER");
  assert.deepEqual([statements, skipped], [2, 1]);
  assert.ok(schema.tables.ok);
});

test("5 MB schema parses within the NFR-01 budget", () => {
  const table = (i: number) => `CREATE TABLE [dbo].[T${i}]([Id] [int] IDENTITY(1,1) NOT NULL, ${Array.from({ length: 15 }, (_, c) => `[C${c}] [nvarchar](100) NULL`).join(", ")}, CONSTRAINT [PK_T${i}] PRIMARY KEY CLUSTERED ([Id] ASC))\nGO\nCREATE NONCLUSTERED INDEX [IX_T${i}_C1] ON [dbo].[T${i}] ([C1]) INCLUDE ([C2])\nGO\n`;
  const parts: string[] = [];
  let size = 0;
  for (let i = 0; size < 5 * 1024 * 1024; i++) size += parts[parts.push(table(i)) - 1].length;
  const start = performance.now();
  const { schema, skipped } = parseSchemaFile(parts.join(""), "big.sql", "SQLSERVER");
  const ms = performance.now() - start;
  console.log(`5 MB: ${Object.keys(schema.tables).length} tables in ${ms.toFixed(0)} ms`);
  assert.equal(skipped, 0);
  assert.ok(ms < 1000, `took ${ms.toFixed(0)} ms`); // 300 ms target; slack for slow CI runners
});

test("JSON schema file", () => {
  const json = JSON.stringify({ dialect: "POSTGRESQL", tables: { orders: { columns: { id: { name: "id", dataType: "int", isNullable: false, isPrimaryKey: true } } } } });
  const { schema } = parseSchemaFile(json, "schema.json", "SQLSERVER");
  assert.equal(schema.dialect, "POSTGRESQL");
  assert.deepEqual(schema.tables.orders.indexes, []);
  assert.equal(schema.tables.orders.tableName, "orders");
  assert.throws(() => parseSchemaFile("{}", "schema.json", "SQLSERVER"), /tables/);
});
