using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarCifAEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cif",
                table: "Empresas",
                type: "TEXT",
                maxLength: 9,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_Cif",
                table: "Empresas",
                column: "Cif",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Empresas_Cif",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "Cif",
                table: "Empresas");
        }
    }
}
