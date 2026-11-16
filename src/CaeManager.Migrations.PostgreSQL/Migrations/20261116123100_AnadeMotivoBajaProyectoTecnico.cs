using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// D-4 sobre la #2005 (decisión del 2026-11-15): el motivo de la baja de un participante de Proyecto
    /// (<c>MotivoBajaProyectoTecnico</c>: 1 = desasignado a mano, 2 = fin del evento), para que Proyectos distinga la baja
    /// automática del Sistema de una desasignación hecha por una persona.
    /// <para>
    /// Añade a <c>ProyectosTecnicos</c> una columna entera que admite nulo, sin valor por defecto ni relleno: las bajas
    /// anteriores no tienen motivo conocido y se muestran como hasta ahora, solo con la fecha. Compatible con la ranura
    /// anterior durante el relevo azul/verde: no la lee ni la escribe, y sus bajas quedan con motivo nulo.
    /// </para>
    /// </summary>
    public partial class AnadeMotivoBajaProyectoTecnico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MotivoBaja",
                table: "ProyectosTecnicos",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MotivoBaja",
                table: "ProyectosTecnicos");
        }
    }
}
