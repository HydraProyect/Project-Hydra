using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Paso 1 del rediseño Communication Workspace (docs/COMUNICACIONES.md
    /// § 16.1/13.1): cada Mensaje pasa a conocer su propio Canal — hoy
    /// siempre coincide con el de su Conversacion (todavía no hay hilos
    /// mixtos), fundamento para el Conversation Matching Engine (§ 13.2).
    /// El scaffold por defecto ponía <c>defaultValue: 0</c> (Correo) para
    /// TODAS las filas existentes — habría "convertido" en Correo cualquier
    /// mensaje WhatsApp ya persistido. Se reescribe a mano como
    /// nullable→backfill determinista desde la Conversacion padre (mismo
    /// criterio "columna nueva se puebla ANTES de exigirse NOT NULL" que
    /// MigrarCanalGestionAProveedorPlataformaCae) → NOT NULL: el backfill
    /// es 1:1 sin ambigüedad porque cada Mensaje pertenece a exactamente una
    /// Conversacion (ConversacionId NOT NULL), a diferencia del backfill de
    /// aquella migración que sí podía dejar filas sin resolver.
    /// </summary>
    public partial class AgregarCanalAMensaje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Canal",
                table: "Mensajes",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Mensajes" m
                SET "Canal" = c."Canal"
                FROM "Conversaciones" c
                WHERE c."Id" = m."ConversacionId";
                """);

            migrationBuilder.AlterColumn<int>(
                name: "Canal",
                table: "Mensajes",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Canal",
                table: "Mensajes");
        }
    }
}
