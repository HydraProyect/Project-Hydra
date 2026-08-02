using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// P1-14 de docs/business/MATURITY_REVIEW.md: índice parcial orientado al
    /// filtro global sobre Documentos, CHECK XOR del propietario polimórfico,
    /// y pg_trgm + índices GIN para las 32 búsquedas Contains de texto libre
    /// (ver ObtenerCentrosQuery, ObtenerTrabajadoresQuery, ObtenerVehiculosQuery,
    /// BuscarGlobalQuery, ObtenerClientesQuery, ObtenerEmpresasQuery,
    /// ObtenerSubcontratasQuery, ObtenerConversacionesQuery, ObtenerIncidenciasQuery).
    ///
    /// Todo en SQL crudo, a propósito: ninguno de estos tres cambios se declara
    /// en el modelo Fluent de EF Core (Configurations/CaeManagerDbContext), así
    /// que el snapshot del modelo no cambia y esta migración no puede arrastrar
    /// un desajuste de "has-pending-model-changes" — el índice GIN con
    /// gin_trgm_ops en particular no tiene traducción a LINQ que EF necesite
    /// conocer, y el CHECK constraint es un backstop de integridad que EF no
    /// valida del lado cliente. Coste: un futuro "dotnet ef migrations add" no
    /// "ve" estos objetos — aceptable porque nada en el modelo debería tocarlos.
    /// </summary>
    public partial class RendimientoBusquedasYCheckXorDocumento : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // Documentos: alertas/dashboards de vencimiento filtran siempre por
            // TenantId + FechaVencimiento sobre los no eliminados — el índice
            // simple existente (FechaVencimiento solo) obliga a un filtro extra
            // en memoria por tenant sobre un índice que mezcla todos los tenants.
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Documentos_TenantId_FechaVencimiento"
                ON "Documentos" ("TenantId", "FechaVencimiento")
                WHERE NOT "EstaEliminado";
                """);

            // CHECK XOR del propietario polimórfico (DATABASE.md documentaba la
            // intención, nunca fue una constraint real — MATURITY_REVIEW.md § hallazgo:
            // "la BD acepta un documento con dos propietarios o ninguno").
            // num_nonnulls() es más difícil de escribir mal que una suma de CASE WHEN.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Documentos" ADD CONSTRAINT "CK_Documentos_PropietarioXor"
                CHECK (num_nonnulls("TrabajadorId", "ClienteId", "EmpresaId", "VehiculoId", "ProyectoId") = 1);
                """);

            CrearIndiceTrigram(migrationBuilder, "Centros", "Nombre");
            CrearIndiceTrigram(migrationBuilder, "Trabajadores", "Nombre");
            CrearIndiceTrigram(migrationBuilder, "Trabajadores", "Apellidos");
            CrearIndiceTrigram(migrationBuilder, "Trabajadores", "Dni");
            CrearIndiceTrigram(migrationBuilder, "Trabajadores", "Alias");
            CrearIndiceTrigram(migrationBuilder, "Vehiculos", "Nombre");
            CrearIndiceTrigram(migrationBuilder, "Vehiculos", "Modelo");
            CrearIndiceTrigram(migrationBuilder, "Vehiculos", "NumeroPlaca");
            CrearIndiceTrigram(migrationBuilder, "Clientes", "RazonSocial");
            CrearIndiceTrigram(migrationBuilder, "Empresas", "RazonSocial");
            CrearIndiceTrigram(migrationBuilder, "Subcontratas", "RazonSocial");
            CrearIndiceTrigram(migrationBuilder, "ConversacionesCorreo", "Asunto");
            CrearIndiceTrigram(migrationBuilder, "Incidencias", "Descripcion");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            EliminarIndiceTrigram(migrationBuilder, "Incidencias", "Descripcion");
            EliminarIndiceTrigram(migrationBuilder, "ConversacionesCorreo", "Asunto");
            EliminarIndiceTrigram(migrationBuilder, "Subcontratas", "RazonSocial");
            EliminarIndiceTrigram(migrationBuilder, "Empresas", "RazonSocial");
            EliminarIndiceTrigram(migrationBuilder, "Clientes", "RazonSocial");
            EliminarIndiceTrigram(migrationBuilder, "Vehiculos", "NumeroPlaca");
            EliminarIndiceTrigram(migrationBuilder, "Vehiculos", "Modelo");
            EliminarIndiceTrigram(migrationBuilder, "Vehiculos", "Nombre");
            EliminarIndiceTrigram(migrationBuilder, "Trabajadores", "Alias");
            EliminarIndiceTrigram(migrationBuilder, "Trabajadores", "Dni");
            EliminarIndiceTrigram(migrationBuilder, "Trabajadores", "Apellidos");
            EliminarIndiceTrigram(migrationBuilder, "Trabajadores", "Nombre");
            EliminarIndiceTrigram(migrationBuilder, "Centros", "Nombre");

            migrationBuilder.Sql("ALTER TABLE \"Documentos\" DROP CONSTRAINT \"CK_Documentos_PropietarioXor\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Documentos_TenantId_FechaVencimiento\";");

            // No se desactiva pg_trgm: pudo haberse dejado activa por otra
            // migración futura entre medias, y DROP EXTENSION solo funciona si
            // nada más depende de ella.
        }

        private static void CrearIndiceTrigram(MigrationBuilder migrationBuilder, string tabla, string columna) =>
            migrationBuilder.Sql(
                $"""
                CREATE INDEX IF NOT EXISTS "IX_{tabla}_{columna}_Trgm"
                ON "{tabla}" USING gin ("{columna}" gin_trgm_ops);
                """);

        private static void EliminarIndiceTrigram(MigrationBuilder migrationBuilder, string tabla, string columna) =>
            migrationBuilder.Sql($"DROP INDEX IF EXISTS \"IX_{tabla}_{columna}_Trgm\";");
    }
}
