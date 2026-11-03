# QueryIndex Advisor

Menganalisis query SQL (SQL Server, PostgreSQL, MySQL) atau ekspresi C# LINQ, lalu merekomendasikan indeks (single, composite, covering) beserta skrip `CREATE INDEX` siap pakai. Tanpa login, dan file skema diproses di browser. Spesifikasi lengkap ada di [PRD.md](PRD.md).

| Folder | Isi |
| --- | --- |
| [`api/`](api) | Backend stateless .NET 10: `POST /api/analyze`. Parser: ScriptDom (T-SQL), SqlParserCS (PostgreSQL/MySQL), Roslyn (LINQ). |
| [`web/`](web) | Frontend Next.js + Monaco: editor, drawer skema (Web Worker), riwayat di `localStorage`. |
| [`docker-compose.yml`](docker-compose.yml) | Menjalankan keduanya sekaligus. |

## Menjalankan secara lokal

Prasyarat: .NET 10 SDK dan Node.js 24.

```bash
# Terminal 1: API di http://localhost:5080
dotnet run --project api/QueryIndexAdvisor.Api

# Terminal 2: web di http://localhost:3001
cd web
npm install
npm run dev
```

Dengan Docker:

```bash
docker compose up --build   # web: http://localhost:3001, API: http://localhost:5080
```

## Test

```bash
cd api && dotnet test   # unit test + benchmark corpus (KPI akurasi PRD §1)
cd web && npm test      # parser skema, scanner DDL cepat, LINQ inspector
```

Benchmark corpus ada di `api/QueryIndexAdvisor.Tests/corpus.*.json`, satu file per kombinasi bahasa dan dialek. Untuk menambah kasus, cukup tambahkan entri baru di `cases`. Setiap kasus diuji kecocokan persisnya: tabel, urutan kolom kunci, kolom `INCLUDE`, status *covered*, kode peringatan, dan (opsional) teks DDL.

## Konfigurasi

**API** (environment variable; `__` adalah pemisah bagian konfigurasi .NET):

| Variable | Default | Keterangan |
| --- | --- | --- |
| `Cors__AllowedOrigins__0` | `http://localhost:3001` | Origin frontend yang diizinkan. Tambah origin lain dengan `__1`, `__2`, dst. |
| `RateLimit__PermitPerMinute` | `60` | Batas request per menit per IP (NFR-03). |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | Set `true` jika API berada di belakang reverse proxy (Fly.io, Render, Nginx). Lihat catatan di bawah. |
| `ASPNETCORE_HTTP_PORTS` | `8080` di container | Port HTTP. |

**Web** (dibaca saat *build*, bukan saat runtime):

| Variable | Default | Keterangan |
| --- | --- | --- |
| `NEXT_PUBLIC_API_URL` | `http://localhost:5080` | URL publik API. Browser memanggil API langsung, tidak lewat server Next.js. |

## Deploy

**Web ke Vercel**
1. Import repo dan set *Root Directory* ke `web`.
2. Isi `NEXT_PUBLIC_API_URL` dengan URL API, misalnya `https://queryindex-api.fly.dev`.

**API ke Fly.io, Render, atau VPS**
1. Deploy dari [`api/Dockerfile`](api/Dockerfile). Container listen di port `8080`, dan health check tersedia di `GET /health`.
2. Set environment variable:
   - `Cors__AllowedOrigins__0` ke URL frontend (misalnya `https://queryindex.vercel.app`).
   - `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
3. Jalankan **satu instance** saja. Rate limiter disimpan di memori proses, jadi dengan beberapa instance setiap instance punya kuota sendiri. Untuk *scale out*, rate limiter perlu dipindah ke Redis/Valkey (Post-MVP di PRD §8).

> **Kenapa forwarded headers perlu diaktifkan?** Tanpa itu, semua request terlihat berasal dari IP proxy, sehingga seluruh pengguna berbagi satu kuota 60 request/menit. Saat aktif, hanya alamat paling kanan di `X-Forwarded-For` (yang ditambahkan proxy) yang dipakai, sehingga klien tidak bisa memalsukan IP. Aktifkan **hanya** jika API tidak bisa diakses langsung tanpa melewati proxy.

## API

`POST /api/analyze`

```json
{
  "queryType": "SQL",
  "dialect": "POSTGRESQL",
  "query": "SELECT id, total FROM orders WHERE customer_id = $1",
  "schema": null
}
```

- `queryType`: `SQL` atau `LINQ`.
- `dialect`: `SQLSERVER`, `POSTGRESQL`, atau `MYSQL`.
- `schema`: opsional, berformat `CompactSchemaMetadataDTO` (PRD §6.A).

Respons berisi `errors`, `warnings` (beserta posisi baris/kolom), `recommendations` (kolom equality/range/sort/include, urutan kunci, nama indeks, DDL, dan indeks eksisting yang sudah mencakupnya), serta `executionTimeMs`. Bentuk lengkapnya ada di [`Models.cs`](api/QueryIndexAdvisor.Api/Models.cs).

## Privasi

- File skema dibaca dan diurai di Web Worker browser, dan tidak pernah diunggah.
- Setiap request analisis hanya membawa metadata tabel yang disebut di query (nama tabel/kolom, tipe data, indeks).
- Backend tidak menyimpan apa pun: tidak ada database, dan isi request tidak di-log.
- Riwayat (maksimal 20 entri) hanya disimpan di `localStorage` browser pengguna.

## Batasan yang diketahui

- **LINQ dianalisis per sintaks, tanpa kompilasi.** Nama tabel dan kolom mengikuti konvensi EF Core: nama DbSet = nama tabel, navigasi `X` di-join lewat `XId = Id`. Lampirkan skema agar nama tabel yang sebenarnya dipakai dan navigasi bisa dibedakan dari kolom.
- **Sumber query LINQ harus terlihat seperti DbContext atau repository** (`db`, `_context`, `ctx`, `repo`, ...). Tujuannya agar koleksi di memori tidak dianggap tabel.
- **Query yang dipecah ke beberapa variabel** (`var q = db.Orders.Where(...); q.OrderBy(...)`) hanya dianalisis bagian pertamanya.
- **`UPDATE`/`DELETE` hanya mendapat rekomendasi kolom kunci**, tanpa `INCLUDE`.
- **`OR` lintas kolom hanya diberi peringatan**, tidak dibuatkan rekomendasi.
- **MySQL tidak punya `INCLUDE`.** Kolom payload ditambahkan ke ujung key, kecuali kolom TEXT/BLOB/JSON.

## Lisensi

[MIT](LICENSE)
