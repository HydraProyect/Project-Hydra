using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG universal (ADR-011 § 8.9): el invariante de alcance global
    /// de <c>ConcesionPrivilegio</c> pasa a la base. Solo <c>AdminPlataforma</c>
    /// y <c>SoporteLectura</c> admiten alcance global, y la concesión global de
    /// <c>SoporteLectura</c> siempre caduca. Las filas existentes cumplen: hasta
    /// hoy la única concesión global posible era <c>AdminPlataforma</c>.
    /// </summary>
    public partial class SoporteLecturaGlobalConCheckDeAlcance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas",
                table: "ConcesionesPrivilegio",
                sql: "NOT \"EsAlcanceGlobal\" OR \"Capacidad\" IN ('AdminPlataforma', 'SoporteLectura')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConcesionesPrivilegio_SoporteGlobalConVigenciaFinita",
                table: "ConcesionesPrivilegio",
                sql: "NOT (\"EsAlcanceGlobal\" AND \"Capacidad\" = 'SoporteLectura') OR \"VigenciaHasta\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas",
                table: "ConcesionesPrivilegio");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConcesionesPrivilegio_SoporteGlobalConVigenciaFinita",
                table: "ConcesionesPrivilegio");
        }
    }
}
