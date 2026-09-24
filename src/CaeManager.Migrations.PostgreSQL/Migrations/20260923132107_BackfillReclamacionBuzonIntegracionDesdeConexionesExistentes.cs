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
    /// excluye explícitamente) no eliminado (<c>EstaEliminado</c>) y NO
    /// <c>Deshabilitada</c> (hallazgo de la ronda 2 de Codex sobre PR #820):
    /// <c>DesconectarBuzonCommand</c> libera la reclamación exactamente
    /// cuando deja la conexión en ese estado, así que backfillear una
    /// conexión ya deshabilitada antes de este incremento reconstruiría una
    /// reclamación que la lógica nueva considera liberada — bloqueando la
    /// reconexión del mismo buzón, incluida la del propio Tenant vía
    /// <c>ConectarBuzonMicrosoft365Command</c>. <c>Estado</c> se guarda sin
    /// conversión (int por declaración del enum): <c>Deshabilitada = 1</c>.
    /// Aplica el mismo criterio de normalización que
    /// <c>ReclamacionBuzonIntegracion</c> en runtime (minúsculas, recortado).
    ///
    /// Si el bug que este incremento cierra ya dejó dos Tenants compartiendo
    /// el mismo buzón antes de este backfill, <c>ON CONFLICT DO NOTHING</c>
    /// resuelve determinísticamente a favor del más antiguo (<c>ORDER BY
    /// "CreadoEnUtc" ASC, "Id" ASC</c> dentro del mismo INSERT — PostgreSQL
    /// procesa las filas del SELECT en ese orden a efectos de conflicto; el
    /// desempate por <c>"Id"</c>, hallazgo de la ronda 2 de Codex, cubre el
    /// caso de dos conexiones preexistentes con el mismo <c>CreadoEnUtc</c>,
    /// donde el orden por fecha sola no es determinista): ese Tenant
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
                WHERE ci."Proveedor" = 0 AND ci."Estado" <> 1 AND NOT ci."EstaEliminado"
                ORDER BY ci."CreadoEnUtc" ASC, ci."Id" ASC
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
