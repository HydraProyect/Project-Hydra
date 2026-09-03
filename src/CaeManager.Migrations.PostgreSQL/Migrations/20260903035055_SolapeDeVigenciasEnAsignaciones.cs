using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// DEC-19 (REC-064, auditoría Módulo 5 hallazgo #5): dos vigencias
    /// solapadas del mismo trío (Tenant, Trabajador, Centro) son una
    /// contradicción de datos, ya sea contra otra fila activa o contra una ya
    /// cerrada — el hueco que <c>IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa</c>
    /// nunca cubrió (ver <c>SolapamientoDeAsignacionesTests</c>). El dominio y
    /// los tres escritores (<c>CrearAsignacionCommand</c>,
    /// <c>CrearAsignacionesCommand</c>, <c>EjecutarImportacionCommand</c>) ya
    /// rechazan el solape a nivel de aplicación; este <c>EXCLUDE</c> es el
    /// backstop contra la carrera concurrente que ninguna comprobación en
    /// aplicación puede cerrar por sí sola (dos requests que se solapan en el
    /// tiempo, cada una vería "no hay solape" antes de que la otra confirme).
    ///
    /// El límite superior del rango es EXCLUSIVO a propósito —
    /// <c>daterange(FechaAlta, FechaBaja, '[)')</c>—: dar de baja hoy y
    /// reasignar hoy mismo el mismo trío no es un solape (ver
    /// ReasignarMismoDiaTests, cuyo bug real de índice esta migración no debe
    /// reintroducir). <c>btree_gist</c> es necesario porque un <c>EXCLUDE
    /// USING gist</c> no tiene clase de operador nativa para igualdad de
    /// <c>uuid</c>; la extensión se la da (mismo patrón que
    /// RendimientoBusquedasYCheckXorDocumento con <c>pg_trgm</c>).
    ///
    /// Esta restricción SÍ se valida contra las filas existentes al crearse
    /// (un EXCLUDE no admite <c>NOT VALID</c>): si algún tenant tiene datos
    /// que la violan, la migración falla aquí, en vez de desplegar una
    /// invariante que ya está rota — la caracterización previa (REC-064 §9.2)
    /// existe precisamente para que esto no ocurra en producción.
    /// </summary>
    public partial class SolapeDeVigenciasEnAsignaciones : Migration
    {
        private const string NombreRestriccion = "EX_Asignaciones_SinSolapeVigencia";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.Sql(
                $"""
                ALTER TABLE "Asignaciones" ADD CONSTRAINT "{NombreRestriccion}"
                EXCLUDE USING gist (
                    "TenantId" WITH =,
                    "TrabajadorId" WITH =,
                    "CentroId" WITH =,
                    daterange("FechaAlta", "FechaBaja", '[)') WITH &&
                );
                """);

            // La invariante ya no depende de la unicidad de este índice (ver
            // AsignacionConfiguration): se conserva solo por rendimiento, sin
            // la clave de "a lo sumo una fila activa por trío" que el
            // EXCLUDE de arriba ya garantiza con más precisión (incluidas las
            // cerradas).
            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa",
                table: "Asignaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId", "CentroId" },
                filter: "\"FechaBaja\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa",
                table: "Asignaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId", "CentroId" },
                unique: true,
                filter: "\"FechaBaja\" IS NULL");

            migrationBuilder.Sql($"ALTER TABLE \"Asignaciones\" DROP CONSTRAINT \"{NombreRestriccion}\";");

            // No se desactiva btree_gist: pudo haberse dejado activa por otra
            // migración futura entre medias, y DROP EXTENSION solo funciona
            // si nada más depende de ella (mismo criterio que pg_trgm en
            // RendimientoBusquedasYCheckXorDocumento).
        }
    }
}
