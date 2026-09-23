using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Backfill (hallazgo P1 de la revisión Codex sobre PR #820, incremento 1
    /// de PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4): la migración que crea
    /// <c>ReclamacionesBuzonIntegracion</c> deja la tabla vacía — cualquier
    /// <c>ConexionIntegracion</c> de Microsoft 365 conectada ANTES de ese
    /// cambio no tiene reclamación, así que la guarda de unicidad global solo
    /// protege buzones conectados a partir de ahora, no los que ya existían.
    ///
    /// Reconstruye una reclamación por cada buzón M365 (<c>Proveedor = 0</c>;
    /// WhatsApp reutiliza la misma columna para el número E.164, así que se
    /// excluye explícitamente) no eliminado (<c>EstaEliminado</c>), con el
    /// mismo criterio de normalización que <c>ReclamacionBuzonIntegracion</c>
    /// aplica en runtime (minúsculas, recortado).
    ///
    /// Si el bug que este incremento cierra ya dejó dos Tenants compartiendo
    /// el mismo buzón antes de este backfill, <c>ON CONFLICT DO NOTHING</c>
    /// resuelve determinísticamente a favor del más antiguo (<c>ORDER BY
    /// "CreadoEnUtc" ASC</c> dentro del mismo INSERT — PostgreSQL procesa las
    /// filas del SELECT en ese orden a efectos de conflicto): ese Tenant
    /// conserva la reclamación y el buzón compartido queda documentado, no
    /// oculto. El otro Tenant no pierde su conexión existente (sigue
    /// Habilitada); solo queda sin reclamación registrada, igual que estaba
    /// antes de este incremento.
    ///
    /// Aditiva y solo INSERT (mismo contrato que
    /// <see cref="BackfillVinculosExtraccionIaCacheDesdeAuditoria"/>):
    /// <c>ON CONFLICT DO NOTHING</c> la hace además idempotente frente a un
    /// reintento. <c>Down()</c> revierte esquema, nunca datos.
    /// </summary>
    public partial class BackfillReclamacionBuzonIntegracionDesdeConexionesExistentes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO "ReclamacionesBuzonIntegracion" ("Id", "BuzonEmail", "TenantPropietarioId", "ConexionIntegracionId", "ReclamadoEnUtc")
                SELECT gen_random_uuid(), LOWER(TRIM(BOTH FROM ci."BuzonEmail")), ci."TenantId", ci."Id", ci."CreadoEnUtc"
                FROM "ConexionesIntegracion" ci
                WHERE ci."Proveedor" = 0 AND NOT ci."EstaEliminado"
                ORDER BY ci."CreadoEnUtc" ASC
                ON CONFLICT ("BuzonEmail") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A propósito sin DELETE — ver el comentario de clase.
        }
    }
}
