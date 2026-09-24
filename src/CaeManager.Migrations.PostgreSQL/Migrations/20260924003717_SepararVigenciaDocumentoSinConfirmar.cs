using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Separa los dos significados que tenía <c>Documentos.FechaVencimiento IS NULL</c>:
    /// «no caduca» (confirmado por el Gestor CAE) y «nadie ha anotado cuándo
    /// caduca». La columna nueva <c>EstadoVigencia</c> guarda cuál de los dos
    /// es (<c>EstadoVigenciaDocumento</c>: 0 SinConfirmar, 1 NoCaduca,
    /// 2 VenceEnFecha) y la CHECK impide que vuelvan a mezclarse.
    ///
    /// <para>
    /// <b>Relleno conservador</b>: una fila con fecha pasa a VenceEnFecha; una
    /// sin fecha, a SinConfirmar — nadie confirmó nunca que no caducara, así
    /// que no se inventa esa confirmación.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué el relleno recorre tenants</b>: <c>Documentos</c> tiene
    /// <c>FORCE ROW LEVEL SECURITY</c> y el rol que migra no tiene
    /// <c>BYPASSRLS</c>; sin <c>app.tenant_id</c>, el UPDATE no vería ninguna
    /// fila. Mismo patrón que <c>F3cRetiradaClientesSubcontratasLegacy</c>:
    /// <c>set_config(..., is_local =&gt; true)</c> por cada Tenant, sin
    /// desactivar RLS. La CHECK se añade DESPUÉS y su validación no está
    /// sujeta a RLS: una fila con fecha que el recorrido no alcanzara (TenantId
    /// ausente de <c>Tenants</c>) hace fallar la migración en vez de quedar
    /// mal clasificada en silencio.
    /// </para>
    /// </summary>
    public partial class SepararVigenciaDocumentoSinConfirmar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EstadoVigencia",
                table: "Documentos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    tenant uuid;
                BEGIN
                    FOR tenant IN SELECT "Id" FROM "Tenants" LOOP
                        PERFORM set_config('app.tenant_id', tenant::text, true);
                        UPDATE "Documentos" SET "EstadoVigencia" = 2
                        WHERE "FechaVencimiento" IS NOT NULL;
                    END LOOP;
                    PERFORM set_config('app.tenant_id', '', true);
                END $$;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documentos_EstadoVigenciaCoherente",
                table: "Documentos",
                sql: "(\"EstadoVigencia\" = 2 AND \"FechaVencimiento\" IS NOT NULL) OR (\"EstadoVigencia\" IN (0, 1) AND \"FechaVencimiento\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Documentos_EstadoVigenciaCoherente",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "EstadoVigencia",
                table: "Documentos");
        }
    }
}
