# Product Requirement Document (PRD): QueryIndex Advisor

## 1. Overview

* **Background**: Backend Engineers dan Database Administrators (DBA) kerap menghadapi degradasi performa aplikasi akibat eksekusi query SQL yang tidak efisien atau kueri ORM (seperti C# LINQ) yang menghasilkan *full table scan*. Penentuan indeks tabel secara manual membutuhkan waktu untuk menganalisis AST (*Abstract Syntax Tree*), predikat filter, klausa join, dan kardinalitas kolom.
* **Problem Statement**: Penentuan indeks tanpa alat bantu berisiko memunculkan masalah *over-indexing*, pembuatan indeks yang duplikat/redundant, atau mengabaikan aturan predikat *sargable*. Pengembang membutuhkan alat instan yang aman tanpa hambatan pendaftaran (*no-login friction*) dan tanpa risiko file skema database tersimpan di server pihak ketiga.
* **Objective**: Membangun aplikasi web dan API service 100% *open-source* yang secara otomatis menganalisis query SQL mentah maupun ekspresi C# LINQ, merekomendasikan pembuatan indeks (*single/composite/covering index*), serta menyediakan skrip DDL `CREATE INDEX` yang disesuaikan dengan skema tabel dan dialek RDBMS target secara *stateless* dan *privacy-first*.
* **Target Audience**: Backend Engineers (.NET/SQL), Database Administrators, System Architects, dan DevOps Engineers.
* **Success Metrics (KPIs)**:
  * Akurasi rekomendasi indeks > 92% sesuai best practice penindeksan RDBMS, diukur terhadap *benchmark corpus* (kumpulan pasangan query → indeks yang diharapkan) yang disimpan di repo dan dijalankan otomatis di CI.
  * Waktu analisis kueri < 1,5 detik per permintaan (sampai 500 baris kode).
  * *Zero-friction adoption*: 100% fitur utama dapat diakses secara instan tanpa login/pendaftaran akun (*Guest Mode*).
  * *Zero raw schema upload*: file skema mentah 0% dikirim ke server, diproses dengan arsitektur *Client-Side In-Memory Schema Processing*. Hanya metadata ringkas (nama tabel, kolom, tipe data, indeks) yang dikirim per request dan tidak disimpan (lihat NFR-02).
  * Adopsi 100% pustaka *open-source* tanpa ketergantungan API berbayar pihak ketiga.

---

## 2. Requirement

### Functional Requirements (FR)

* **FR-01 (Guest-First Access & Anonymous Session)**:
  * Pengguna dapat menggunakan seluruh fungsi analisis query, unggah skema, dan pembuatan DDL secara langsung tanpa perlu *sign-up* atau *login*.
  * Riwayat analisis lokal disimpan secara aman di browser pengguna (`localStorage`).
* **FR-02 (Client-Side In-Memory Schema Parsing)**:
  * Pengunggahan file skema (`.sql` DDL / `.json`) diproses sepenuhnya secara *in-memory* di browser menggunakan *Web Worker Thread*.
  * File skema mentah tidak pernah diunggah ke server. Yang dikirim ke backend hanya `CompactSchemaMetadataDTO` (nama tabel, kolom, tipe data, indeks eksisting) bersama request analisis, dan tidak disimpan di penyimpanan permanen (*Zero-Server Retention*).
  * Format file `.json` mengikuti struktur `CompactSchemaMetadataDTO` (lihat §6.A).
* **FR-03 (Dual-Parser Engine - LINQ & Multi-Dialect SQL)**:
  * Memproses ekspresi C# LINQ menggunakan Roslyn C# SDK (`Microsoft.CodeAnalysis.CSharp`) secara **syntax-only**. Snippet tidak dikompilasi (tidak ada DbContext/tipe entity), sehingga pemetaan `db.Orders` → tabel `Orders` dan properti → kolom memakai konvensi penamaan, divalidasi terhadap skema jika tersedia.
  * Memproses query SQL multi-dialect: T-SQL via `ScriptDom`; PostgreSQL & MySQL via `SqlParserCS` (port .NET dari `sqlparser-rs`, managed, satu library untuk kedua dialek). Dua bug precedence pada versi 0.6.5 (`NOT ... AND`, `LIKE ... AND`) dikoreksi di analyzer dan dikunci oleh test corpus.
* **FR-04 (Rule-Based Recommendation & Sargability Inspector)**:
  * Mengisolasi kolom pada klausa `WHERE`, `JOIN`, `ORDER BY`, dan `GROUP BY`.
  * Menerapkan aturan *Equality-first*: Kolom predikat samadengan (`=`, `==`) diletakkan di depan pada *Composite Index*, diikuti kolom rentang/range (`>`, `<`, `LIKE`, `BETWEEN`).
  * Menentukan kolom pendukung ke klausa `INCLUDE` (Covering Index).
  * Mendeteksi ekspresi non-sargable pada LINQ (misal: penggunaan `.ToString()`, `.ToLower()`, atau eksekusi fungsi C# di dalam `.Where()`) dan memberikan garis keriting kuning (*Warning Marker*) di editor.
  * Inspector *real-time* (saat mengetik) berjalan **sepenuhnya client-side** dengan pencocokan pola ringan, sehingga tidak memanggil backend dan tidak mengonsumsi kuota rate limit. Analisis lengkap berbasis AST tetap dilakukan backend saat *Analyze*.
* **FR-05 (Duplicate & Overlapping Index Filter)**:
  * Membandingkan usulan indeks baru terhadap daftar indeks yang sudah ada di skema.
  * Memberikan peringatan jika indeks yang diusulkan sudah ter-cover oleh *existing index*, menggunakan aturan **left-prefix**: indeks `(A, B, C)` meng-cover usulan `(A, B)`, tetapi tidak meng-cover `(B)` atau `(B, A)`.
* **FR-06 (DDL Generator & Export)**:
  * Menggenerasi skrip DDL `CREATE INDEX` yang valid secara otomatis sesuai RDBMS target (SQL Server, PostgreSQL, MySQL).
  * Klausa `INCLUDE` hanya digenerasi untuk SQL Server dan PostgreSQL 11+. Untuk MySQL (tidak mendukung `INCLUDE`), kolom payload ditambahkan di akhir key columns atau dilewati dengan catatan di hasil analisis.
  * Nama indeks otomatis dipotong sesuai batas panjang identifier dialek (PostgreSQL 63, MySQL 64, SQL Server 128 karakter) dengan sufiks hash pendek agar tetap unik.
  * Menyediakan fitur *One-Click Copy* dan *Export to .sql File*.

### Non-Functional Requirements (NFR)

* **NFR-01 (Performance & Latency)**: Latensi eksekusi parsing dan rekomendasi tidak boleh melebihi 1.500 ms. Parsing skema di *Web Worker* client-side ditargetkan selesai dalam < 300 ms untuk file skema hingga 5MB (target awal, wajib diverifikasi dengan benchmark parser terpilih).
* **NFR-02 (Security & Zero-Data Retention)**: Backend bersifat *stateless*. Metadata skema yang dikirim bersama request hanya diproses di RAM server selama eksekusi request dan langsung dibuang (*garbage collected*).
* **NFR-03 (Rate Limiting & Abuse Protection)**: Backend menerapkan IP-based rate limiting (misal: maksimum 60 request/menit per IP) untuk melindungi API dari eksploitasi *guest access*. Fase MVP memakai rate limiter bawaan ASP.NET Core (in-memory, single instance); Redis/Valkey baru ditambahkan saat backend di-scale ke lebih dari satu instance.
* **NFR-04 (Cost Efficiency)**: Arsitektur dirancang 100% berbasis *open-source* tanpa ketergantungan pada API AI/LLM berbayar.
* **NFR-05 (Usability & Accessibility)**: Antarmuka berbasis split-screen editor menggunakan Monaco Editor (VS Code engine) lengkap dengan *syntax highlighting*, *auto-completion*, dan *inline diagnostics*.

---

## 3. Core Feature

| Feature ID | Feature Name | Description | Acceptance Criteria |
| --- | --- | --- | --- |
| **CF-01** | **No-Login Guest Editor Workspace** | Halaman utama yang langsung menyajikan editor tanpa *login-wall*. | Pengguna langsung dapat mempaste query dan mengklik *Analyze* pada kunjungan pertama. |
| **CF-02** | **In-Memory Schema Drawer** | Drawer untuk melepaskan file `.sql` DDL atau `.json` skema tabel. | File diurai di *Web Worker* browser. Menyajikan daftar tabel, kolom, tipe data, dan indeks eksisting tanpa *HTTP file upload* ke server. |
| **CF-03** | **Monaco Schema IntelliSense** | Auto-completion nama tabel dan kolom pada Monaco Editor berdasarkan skema in-memory. | Saat pengguna mengetik di Monaco Editor, nama tabel dan kolom dari skema yang aktif muncul di popup IntelliSense. |
| **CF-04** | **Composite & Covering Index Engine** | Algoritma penentu urutan kolom kunci dan *included columns*. | Menghasilkan nama indeks terstandar `IX_[Table]_[EqualityCols]_[RangeCols]` dengan urutan kolom kunci yang tepat. |
| **CF-05** | **Redundant Index Filter** | Modul pencegah pembuatan indeks yang overlap dengan indeks yang sudah ada. | Jika indeks eksisting sudah mencakup kolom predikat, sistem memberi notifikasi "Index Already Covered" dan menyajikan detail indeks eksisting tersebut. |
| **CF-06** | **Local Session History Manager** | Menyimpan riwayat kueri dan DDL rekomendasi terakhir di browser. | Riwayat tersimpan hingga 20 entri di `localStorage` dan dapat diakses kembali via tombol *History Drawer*. |

---

## 4. Userflow

```
[ Pengguna Membuka Web App QueryIndex Advisor ]
                       │ (Tanpa Login / Instant Access)
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 1. Pilih Input Mode (C# LINQ / Raw SQL) & Target RDBMS      │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. Attach Schema (.sql / .json) - [OPSIONAL]                │
│    └─► File diurai Client-Side via Web Worker               │
│    └─► In-Memory Schema State Active & Monaco IntelliSense ON│
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. Paste / Ketik Kode Query pada Monaco Editor Pane Kiri   │
│    └─► Client-Side Debounce Inspector (Warns Non-Sargable)  │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. Klik "Analyze & Recommend Index" (Ctrl + Enter)          │
│    └─► Sending Stateless Compact Payload to Backend API     │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 5. Pane Kanan Menampilkan Hasil Analisis & DDL:             │
│    ├─► Breakdown Predikat (Equality, Range, Sort, Payload)  │
│    ├─► Peringatan Anti-Pattern & Saran Refactoring LINQ    │
│    ├─► Rekomendasi DDL CREATE INDEX (Read-Only Editor)      │
│    └─► Warning Indeks Duplikat (jika Schema Active)         │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 6. Action: [ Copy DDL ] atau [ Export .sql Script ]          │
│    └─► Sesi Otomatis Tersimpan di LocalStorage Browser      │
└─────────────────────────────────────────────────────────────┘
```

---

## 5. Architecture

Sistem menggunakan pendekatan **Client-Side Heavy Parsing & Stateless Backend API** untuk menjaga skalabilitas tinggi, biaya operasional murah, dan perlindungan privasi data.

```
┌──────────────────────────────────────────────────────────────────────────┐
│ BROWSER / CLIENT SIDE LAYER                                              │
│ React.js / Next.js  +  Monaco Editor  +  Zustand State Store            │
│                                                                          │
│ ┌──────────────────────────────────┐  ┌────────────────────────────────┐ │
│ │ Web Worker Schema Parser Thread  │  │ LocalStorage History Manager   │ │
│ │ (FileReader + node-sql-parser)   │  │ (Max 20 Recent Analyses)       │ │
│ └─────────────────┬────────────────┘  └────────────────────────────────┘ │
└───────────────────┼──────────────────────────────────────────────────────┘
                    │
                    │ Compact Payload HTTP POST /api/analyze
                    │ (rawQueryText + compactSchemaMetadata DTO)
                    ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ BACKEND STATELESS API LAYER (.NET 10 Web API)                            │
│                                                                          │
│ ┌──────────────────────────────────┐  ┌────────────────────────────────┐ │
│ │ IP Rate Limiter Middleware       │  │ AST Parser Engine              │ │
│ │ (Fixed Window - In-Memory)       │  │ (Roslyn & TSql ScriptDom)      │ │
│ └──────────────────────────────────┘  └────────────────┬───────────────┘ │
│                                                        │                 │
│                                                        ▼                 │
│                                       ┌────────────────────────────────┐ │
│                                       │ Rule-Based Recommendation      │ │
│                                       │ & Duplicate Index Engine       │ │
│                                       └────────────────┬───────────────┘ │
└────────────────────────────────────────────────────────┼─────────────────┘
                                                         │
                                                         ▼
                                        ┌────────────────────────────────┐
                                        │ DDL Response JSON (Stateless)  │
                                        └────────────────────────────────┘
```

### Komponen Utama Arsitektur:

1. **Web Worker Schema Parser Thread**: Membaca file `.sql` DDL di browser, mengekstrak tabel, kolom, dan indeks eksisting ke dalam memori browser tanpa mengirim isi file fisik ke server.
2. **Monaco Editor State**: Mengelola interaksi kode pengguna, memberikan marker error/warning, dan mendaftarkan *completion items* secara dinamis dari In-Memory Schema State.
3. **IP Rate Limiter Middleware**: Menggunakan algoritma *Fixed Window Rate Limiting* bawaan ASP.NET Core (in-memory) untuk mencegah kelebihan beban akibat penggunaan tanpa autentikasi. Migrasi ke Redis/Valkey saat backend berjalan multi-instance.
4. **Roslyn & ScriptDom Engine (.NET 10)**: Membedah AST kueri LINQ/SQL secara deterministik dan cepat dalam orde milidetik.
5. **Stateless Execution**: Server backend tidak mengelola basis data pengguna; seluruh pemrosesan bersifat *request-response* berbasis RAM.

---

## 6. Database & In-Memory Data Model

### A. Client-Side In-Memory Schema DTO (Typescript)

Struktur data yang disimpan sementara di RAM Browser saat pengguna menempelkan skema:

```typescript
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

export interface CompactSchemaMetadataDTO {
  dialect: 'SQLSERVER' | 'POSTGRESQL' | 'MYSQL';
  tables: Record<string, CompactTableSchema>;
}
```

### B. Anonymous Session Log Database Schema (PostgreSQL - Optional Analytics, Post-MVP)

> **Di luar scope MVP.** Backend MVP sepenuhnya stateless tanpa database. Skema ini hanya diaktifkan bila kelak dibutuhkan statistik performa anonim.

Skema internal PostgreSQL jika pengembang ingin mengumpulkan statistik performa anonim tanpa menyimpan query sensitif pengguna:

```sql
CREATE TABLE anonymous_analytics_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    query_type VARCHAR(20) NOT NULL, -- 'LINQ' atau 'SQL'
    target_rdbms VARCHAR(30) NOT NULL, -- 'SQLSERVER', 'POSTGRESQL', 'MYSQL'
    has_schema_context BOOLEAN NOT NULL DEFAULT FALSE,
    execution_time_ms INT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

---

## 7. Tech Stack

* **Frontend**:
  * **Framework**: React.js / Next.js 14+ (App Router, TypeScript).
  * **Code Editor**: `@monaco-editor/react`.
  * **Client Parser & Worker**: `node-sql-parser` (multi-dialect DDL), Web Workers API.
  * **State Management**: Zustand (In-Memory Schema Store).
  * **Styling & UI**: Tailwind CSS, Lucide Icons, Shadcn UI.
* **Backend Framework**:
  * **Runtime/SDK**: .NET 10 (LTS) Web API (C#). .NET 8 berakhir masa dukungannya pada 10 November 2026.
  * **LINQ Parsing Library**: `Microsoft.CodeAnalysis.CSharp` (Roslyn SDK).
  * **SQL Parsing Library**: `Microsoft.SqlServer.TransactSql.ScriptDom` (T-SQL); `SqlParserCS` (PostgreSQL/MySQL).
  * **Rate Limiting**: `Microsoft.AspNetCore.RateLimiting` (built-in, in-memory). Redis / Valkey saat multi-instance.
* **DevOps & Infrastructure**:
  * **Containerization**: Docker & Docker Compose.
  * **CI/CD**: GitHub Actions.
  * **Hosting Deployment**: Vercel (Frontend Client) + Fly.io / Render / VPS (Backend Stateless API).
  * **Cost Requirement**: 100% Free & Open-Source Stack (Zero Monthly Software License Cost).
* **Repository Structure (Monorepo)**:
  * `/web`: Frontend Next.js.
  * `/api`: Backend .NET 10 Web API.
  * `docker-compose.yml`: menjalankan keduanya secara lokal.

---

## 8. Delivery Roadmap (MVP Phasing)

| Phase | Scope | Output |
| --- | --- | --- |
| **Phase 1** | SQL Server + Raw SQL (ScriptDom) | Engine aturan equality-first, covering index, duplicate filter (left-prefix), DDL generator, endpoint `POST /api/analyze`, benchmark corpus awal. |
| **Phase 2** | Frontend Workspace | Monaco split-screen, Schema Drawer + Web Worker, IntelliSense skema, History Drawer (`localStorage`), Copy/Export DDL. |
| **Phase 3** | C# LINQ (Roslyn) | Parsing LINQ syntax-only, mapping konvensi tabel/kolom, warning non-sargable (client-side inspector + analisis backend). |
| **Phase 4** | PostgreSQL & MySQL | Parser multi-dialect, DDL per dialek (fallback `INCLUDE` MySQL, batas panjang nama indeks). |
| **Post-MVP** | Scale & Analytics | Redis/Valkey rate limiter multi-instance, anonymous analytics (§6.B). |
