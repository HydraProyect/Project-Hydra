using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarCifASubcontrata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cif",
                table: "Subcontratas",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_Cif",
                table: "Subcontratas",
                columns: new[] { "TenantId", "Cif" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subcontratas_TenantId_Cif",
                table: "Subcontratas");

            migrationBuilder.DropColumn(
                name: "Cif",
                table: "Subcontratas");
        }
    }
}
