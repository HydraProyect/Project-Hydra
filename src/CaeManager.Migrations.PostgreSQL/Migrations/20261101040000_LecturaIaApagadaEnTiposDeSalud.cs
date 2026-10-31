using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// DPA 16.4: la lectura por IA de un tipo que revela salud queda apagada salvo que el Tenant
    /// propietario la active por tipo de documento. El UPDATE alcanza las copias por Tenant y los
    /// tipos propios, que HasData no ve. Bajar solo repone las filas de HasData.
    /// </summary>
    public partial class LecturaIaApagadaEnTiposDeSalud : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Las copias son filas de otros Tenants bajo FORCE ROW LEVEL SECURITY: sin esta guarda el UPDATE
            // no vería ninguna fila y la migración pasaría sin apagar nada.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'LecturaIaApagadaEnTiposDeSalud necesita un migrador superusuario o con BYPASSRLS';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                UPDATE "TiposDocumento"
                SET "LecturaIaActiva" = false,
                    "VerificacionIaActiva" = false
                WHERE "Sensibilidad" = 'CategoriaEspecialSalud';
                """);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "LecturaIaActiva",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                column: "LecturaIaActiva",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000006"),
                column: "LecturaIaActiva",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000006"),
                column: "LecturaIaActiva",
                value: true);
        }
    }
}
