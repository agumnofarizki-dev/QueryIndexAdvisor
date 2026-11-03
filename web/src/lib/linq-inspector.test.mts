import assert from "node:assert/strict";
import test from "node:test";
import { inspectLinq } from "./linq-inspector.ts";

const codes = (code: string) => inspectLinq(code).map((h) => h.code);

test("flags wrapped columns, date parts, leading wildcards and client evaluation", () => {
  const code = `db.Customers
  .Where(c => c.Email.ToLower() == email && c.Created.Year == 2024)
  .Where(c => c.Name.Contains(term))
  .AsEnumerable()`;
  const hints = inspectLinq(code);
  assert.deepEqual(hints.map((h) => [h.code, h.line, h.column]), [
    ["NON_SARGABLE", 2, 15],
    ["NON_SARGABLE", 2, 45],
    ["LEADING_WILDCARD", 3, 15],
    ["CLIENT_EVAL", 4, 4],
  ]);
  assert.equal(hints[0].endColumn, 15 + "c.Email.ToLower()".length);
});

test("query syntax range variables, navigation members and nullable dates", () => {
  assert.deepEqual(codes("from o in db.Orders where o.Customer.Name.ToUpper() == n select o.Id"), ["NON_SARGABLE"]);
  assert.deepEqual(codes("db.Orders.Where(o => o.ShippedAt.Value.Year == 2024)"), ["NON_SARGABLE"]);
});

test("ignores non-parameter receivers and sargable calls", () => {
  assert.deepEqual(codes("var e = request.Email.ToLower(); db.Customers.Where(c => c.Name.StartsWith(e) && ids.Contains(c.Id))"), []);
  assert.deepEqual(codes("var total = order.Lines.Count;"), []);
});
