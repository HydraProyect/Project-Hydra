using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// D-9 (decisión del propietario, 2026-10-08): «al reactivar vuelve a ser principal quien lo era antes».
    /// La Asignación de Cartera gana <c>EraPrincipalAlCerrarsePorCascada</c>: si llevaba la marca de principal
    /// cuando la cerró la cascada de su Asignación de Operación (la delegación se desactivó). Es histórico de
    /// una cartera cerrada —lo impone <c>CK_AsignacionesCartera_EraPrincipalSoloCerrada</c>— y no toca la
    /// marca viva, su índice único ni su CHECK.
    ///
    /// <para>
    /// <b>Datos existentes</b>: ninguno se toca. Las operaciones cerradas antes de esta migración no guardan
    /// quién era su principal (el cierre apagaba la marca) y aquí no se inventa: al reactivarlas rige la regla
    /// anterior —una sola cartera de Gestor CAE repuesta nace principal; varias, ninguna—. No cambia ninguna
    /// política RLS.
    /// </para>
    /// </summary>
    public partial class AnadeEraPrincipalAlCerrarsePorCascada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EraPrincipalAlCerrarsePorCascada",
                table: "AsignacionesCartera",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AsignacionesCartera_EraPrincipalSoloCerrada",
                table: "AsignacionesCartera",
                sql: "NOT \"EraPrincipalAlCerrarsePorCascada\" OR \"Estado\" = 'Cerrada'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AsignacionesCartera_EraPrincipalSoloCerrada",
                table: "AsignacionesCartera");

            migrationBuilder.DropColumn(
                name: "EraPrincipalAlCerrarsePorCascada",
                table: "AsignacionesCartera");
        }
    }
}
