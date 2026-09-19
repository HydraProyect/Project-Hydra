using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// El eje "qué clase de actor" en los dos registros que no lo tenían (P41c,
    /// decisión del propietario 2026-09-19). Ortogonal a <c>ViaAcceso</c>: hasta
    /// ahora el barrido de retención y una persona sin claims resueltos
    /// producían filas idénticas —ambas <c>Desconocida</c>, ambas sin usuario—,
    /// y esa columna no puede responder las dos preguntas a la vez.
    ///
    /// <para>
    /// <b>Aditiva y sin reescritura.</b> <c>ADD COLUMN ... NOT NULL DEFAULT</c>
    /// es metadato en PostgreSQL 11+, así que las dos tablas de auditoría —las
    /// que más crecen— no se copian. El defecto <c>'Desconocido'</c> es el valor
    /// seguro para las filas existentes: no afirma ni persona ni máquina, que es
    /// lo único honesto para filas escritas antes de que el eje existiera. No se
    /// infiere nada retroactivamente a partir de <c>UsuarioId IS NULL</c>,
    /// precisamente porque ese nulo es el que confundía los dos casos.
    /// </para>
    ///
    /// <para>
    /// <b>RLS intacta.</b> No toca ninguna política: las dos tablas ya tienen la
    /// suya (<c>aislamiento_tenant</c> sobre <c>cae_app_runtime</c>), y añadir
    /// una columna no la altera ni la reemplaza. Tampoco hace falta GRANT nuevo:
    /// los permisos de PostgreSQL son por tabla, no por columna, en este esquema.
    /// </para>
    /// </summary>
    public partial class TipoActorEnRegistrosDeAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TipoActor",
                table: "RegistrosAuditoria",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Desconocido");

            migrationBuilder.AddColumn<string>(
                name: "TipoActor",
                table: "RegistrosAccesoDocumentoSensible",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Desconocido");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TipoActor",
                table: "RegistrosAuditoria");

            migrationBuilder.DropColumn(
                name: "TipoActor",
                table: "RegistrosAccesoDocumentoSensible");
        }
    }
}
