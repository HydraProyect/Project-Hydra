using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Backfill (hallazgo de revisión Codex sobre REC-036/DEC-34): la
    /// migración que crea <c>ExtraccionesIaCacheDocumentos</c> deja la tabla
    /// vacía — las entradas de <c>ExtraccionesIaCache</c> escritas ANTES de
    /// ese cambio no tienen forma de saber por sí solas qué Documento(s) las
    /// originaron o reutilizaron, así que quedarían invisibles para siempre a
    /// <c>PurgarVinculadosADocumentosAsync</c> aunque su Documento se
    /// anonimizara.
    ///
    /// Sí hay de dónde reconstruirlas: <c>AuditoriasExtraccionIa</c> ya
    /// guarda, para cada llamada a <c>DocumentAIRouterService.ProcesarAsync</c>
    /// (éxito, fallo o acierto de caché), el mismo <c>HashSha256</c> +
    /// <c>TipoEsperado</c> + <c>VersionPipeline</c> que identifica la entrada
    /// de caché, junto con el <c>DocumentoId</c> cuando se conocía —
    /// exactamente la relación que faltaba. El join reconstruye el vínculo
    /// para toda entrada de caché que TODAVÍA exista bajo la misma clave; una
    /// versión de pipeline ya superada simplemente no encuentra una entrada
    /// de caché viva con la que unirse, y no backfillea nada para ella —
    /// correcto, porque esa entrada ya no existe.
    ///
    /// <b>El TipoEsperado de las dos tablas no se escribe igual.</b>
    /// <see cref="Domain.DocumentosIa.ExtraccionIaCache.Crear"/> normaliza
    /// (minúsculas, espacios colapsados) antes de guardar —
    /// <c>DocumentAIRouterService.RegistrarAuditoriaAsync</c> nunca hace esa
    /// normalización, guarda el <c>tipoEsperado</c> tal como llegó. Sin
    /// reproducir aquí la misma normalización, el join no encontraría casi
    /// ninguna coincidencia real. La expresión SQL de abajo es una
    /// aproximación suficiente para un backfill de una sola vez (no la
    /// lógica de negocio en curso, que sigue siendo la de
    /// <c>NormalizarTipoEsperado</c> en C#): no reproduce con exactitud
    /// unicode cada caso límite de <c>ToLowerInvariant</c>, pero coincide
    /// para el vocabulario real de tipos documentales de este catálogo.
    ///
    /// Aditiva y solo INSERT (CLAUDE.md § 9 de HO-036-01: nada de borrar
    /// filas existentes sin elevarlo). <c>ON CONFLICT DO NOTHING</c> la hace
    /// además idempotente frente a un reintento de la migración.
    ///
    /// Down() revierte esquema, nunca datos — mismo contrato ya fijado para
    /// los backfills de F3a (F3aEmpresasUnificadaPreparacion): un DELETE
    /// aquí no podría distinguir, en un Down() invocado en cualquier momento
    /// futuro, los vínculos que puso este backfill de los que ya hubiera
    /// creado el router normalmente sobre las mismas filas.
    /// </summary>
    public partial class BackfillVinculosExtraccionIaCacheDesdeAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO "ExtraccionesIaCacheDocumentos" ("Id", "TenantId", "ExtraccionIaCacheId", "DocumentoId", "CreadaEnUtc")
                SELECT gen_random_uuid(), c."TenantId", c."Id", a."DocumentoId", MIN(a."CreadaEnUtc")
                FROM "AuditoriasExtraccionIa" a
                JOIN "ExtraccionesIaCache" c
                  ON c."TenantId" = a."TenantId"
                 AND c."HashSha256" = a."HashSha256"
                 AND c."TipoEsperado" = LOWER(REGEXP_REPLACE(TRIM(BOTH FROM a."TipoEsperado"), '\s+', ' ', 'g'))
                 AND c."VersionPipeline" = a."VersionPipeline"
                WHERE a."DocumentoId" IS NOT NULL
                GROUP BY c."TenantId", c."Id", a."DocumentoId"
                ON CONFLICT ("TenantId", "ExtraccionIaCacheId", "DocumentoId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A propósito sin DELETE — ver el comentario de clase.
        }
    }
}
