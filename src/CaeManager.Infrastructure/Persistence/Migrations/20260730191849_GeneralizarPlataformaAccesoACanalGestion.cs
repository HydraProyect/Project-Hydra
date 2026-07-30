using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizarPlataformaAccesoACanalGestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename en vez de drop+create: preserva cualquier fila existente
            // (todas eran, por definición, de Tipo Plataforma — backfill 0 abajo).
            migrationBuilder.RenameTable(
                name: "PlataformasAcceso",
                newName: "CanalesGestionDocumental");

            migrationBuilder.RenameIndex(
                name: "IX_PlataformasAcceso_TenantId_CentroId",
                table: "CanalesGestionDocumental",
                newName: "IX_CanalesGestionDocumental_TenantId_CentroId");

            migrationBuilder.AlterColumn<string>(
                name: "NombrePlataforma",
                table: "CanalesGestionDocumental",
                type: "TEXT",
                maxLength: 150,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 150);

            migrationBuilder.AddColumn<int>(
                name: "Tipo",
                table: "CanalesGestionDocumental",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmailsDestinatarios",
                table: "CanalesGestionDocumental",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NombreContacto",
                table: "CanalesGestionDocumental",
                type: "TEXT",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notas",
                table: "CredencialesAccesoSubcontrata",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notas",
                table: "CredencialesAccesoEmpresa",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notas",
                table: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropColumn(
                name: "Notas",
                table: "CredencialesAccesoEmpresa");

            migrationBuilder.DropColumn(
                name: "NombreContacto",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "EmailsDestinatarios",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "CanalesGestionDocumental");

            migrationBuilder.AlterColumn<string>(
                name: "NombrePlataforma",
                table: "CanalesGestionDocumental",
                type: "TEXT",
                maxLength: 150,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 150,
                oldNullable: true);

            migrationBuilder.RenameIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId",
                table: "CanalesGestionDocumental",
                newName: "IX_PlataformasAcceso_TenantId_CentroId");

            migrationBuilder.RenameTable(
                name: "CanalesGestionDocumental",
                newName: "PlataformasAcceso");
        }
    }
}
