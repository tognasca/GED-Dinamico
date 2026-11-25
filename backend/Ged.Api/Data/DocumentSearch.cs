using Ged.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ged.Api.Data;

public static class DocumentSearch
{
    public static async Task InstallAsync(GedDbContext db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            SELECT pg_advisory_xact_lock(496510431);
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrProvider" text;
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrEvidenceJson" jsonb NOT NULL DEFAULT '[]';
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE INDEX IF NOT EXISTS "IX_Documents_SearchVector" ON "Documents"
                USING gin (to_tsvector('portuguese', coalesce("FileName", '') || ' ' || coalesce("OcrText", '')));
            CREATE INDEX IF NOT EXISTS "IX_Documents_SearchTrigram" ON "Documents"
                USING gin ((coalesce("FileName", '') || ' ' || coalesce("OcrText", '')) gin_trgm_ops);
            """);
        await transaction.CommitAsync();
    }

    public static Task<List<GedDocument>> FindAsync(GedDbContext db, Guid tenant, Guid? batchId, Guid? typeId,
        string? field, string? value, string query, int offset, int limit, CancellationToken ct)
    {
        var literal = "%" + query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        return db.Documents.FromSql($"""
            SELECT d.* FROM "Documents" d
            CROSS JOIN websearch_to_tsquery('portuguese', {query}) AS search(terms)
            WHERE d."TenantId" = {tenant}
              AND (CAST({batchId} AS uuid) IS NULL OR d."BatchId" = {batchId})
              AND (CAST({typeId} AS uuid) IS NULL OR d."DocumentTypeId" = {typeId})
              AND (CAST({field} AS text) IS NULL OR
                   (d."Status" = 'Indexed' AND strpos(lower(d."ValuesJson" -> CAST({field} AS text) ->> 'value'), lower({value})) > 0))
              AND (
                to_tsvector('portuguese', coalesce(d."FileName", '') || ' ' || coalesce(d."OcrText", '')) @@ search.terms
                OR (coalesce(d."FileName", '') || ' ' || coalesce(d."OcrText", '')) ILIKE {literal}
                OR {query} <% (coalesce(d."FileName", '') || ' ' || coalesce(d."OcrText", ''))
              )
            ORDER BY (d."FileName" ILIKE {literal}) DESC,
                ts_rank_cd(to_tsvector('portuguese', coalesce(d."FileName", '') || ' ' || coalesce(d."OcrText", '')), search.terms) DESC,
                word_similarity({query}, coalesce(d."FileName", '') || ' ' || coalesce(d."OcrText", '')) DESC,
                d."CreatedAt" DESC, d."Id" DESC
            LIMIT {limit} OFFSET {offset}
            """).AsNoTracking().ToListAsync(ct);
    }
}
