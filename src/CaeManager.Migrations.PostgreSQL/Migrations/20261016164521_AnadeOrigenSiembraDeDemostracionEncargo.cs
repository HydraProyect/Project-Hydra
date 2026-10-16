using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeOrigenSiembraDeDemostracionEncargo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Decision de Chris (2026-10-16, opcion A): la siembra de demostracion del piloto registra el
            // Encargo de administracion de cada Tenant propietario sin Administrador propio con el origen
            // SiembraDeDemostracion y sin quien lo registra, porque no lo registro nadie. El CHECK ata los dos
            // hechos: ni un encargo del producto sin autor, ni uno de la siembra atribuido a una persona. No
            // cambia la RLS (posicion_en_el_encargo) ni los privilegios por columna de cae_app_runtime.
            migrationBuilder.AlterColumn<Guid>(
                name: "RegistradoPorUsuarioId",
                table: "EncargosAdministracion",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EncargosAdministracion_RegistranteSegunOrigen",
                table: "EncargosAdministracion",
                sql: "(\"Origen\" = 'SiembraDeDemostracion') = (\"RegistradoPorUsuarioId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin el origen de la siembra no hay forma de representar esas filas: se eliminan antes de volver a
            // exigir quien lo registra. Solo existen en Tenants con marcador de demo; si algo las referencia
            // todavia (una instruccion de IA activada por encargo), la FK lo impide y el Down falla a la vista.
            migrationBuilder.Sql(
                """DELETE FROM public."EncargosAdministracion" WHERE "Origen" = 'SiembraDeDemostracion';""");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EncargosAdministracion_RegistranteSegunOrigen",
                table: "EncargosAdministracion");

            migrationBuilder.AlterColumn<Guid>(
                name: "RegistradoPorUsuarioId",
                table: "EncargosAdministracion",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
