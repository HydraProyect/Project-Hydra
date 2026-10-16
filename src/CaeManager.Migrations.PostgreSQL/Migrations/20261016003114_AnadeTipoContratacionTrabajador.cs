using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeTipoContratacionTrabajador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TipoContratacion",
                table: "Trabajadores",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Mapeo conservador desde el texto libre TipoVinculo, que se conserva intacto: solo pasan a voluntario o
            // autonomo los textos que lo dicen de forma inequivoca; todo lo demas queda en cuenta ajena (0), el valor
            // por defecto. Corre con la identidad del migrador: con FORCE ROW LEVEL SECURITY y sin app.tenant_id, un
            // rol sin BYPASSRLS no ve filas y el UPDATE no cambia nada, que deja el valor por defecto; no se relaja RLS.
            migrationBuilder.Sql("""
                UPDATE public."Trabajadores" SET "TipoContratacion" = 2
                WHERE "TipoVinculo" ILIKE '%voluntari%';
                UPDATE public."Trabajadores" SET "TipoContratacion" = 1
                WHERE "TipoContratacion" = 0
                  AND ("TipoVinculo" ILIKE '%aut_nomo%' OR "TipoVinculo" ~* '\mRETA\M');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TipoContratacion",
                table: "Trabajadores");
        }
    }
}
