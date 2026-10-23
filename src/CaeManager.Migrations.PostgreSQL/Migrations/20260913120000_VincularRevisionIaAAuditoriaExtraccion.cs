using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    [DbContext(typeof(CaeManagerDbContext))]
    [Migration("20260913120000_VincularRevisionIaAAuditoriaExtraccion")]
    public partial class VincularRevisionIaAAuditoriaExtraccion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AuditoriaExtraccionIaId",
                table: "RevisionesIaDocumento",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RevisionesIaDocumento_AuditoriaExtraccionIaId",
                table: "RevisionesIaDocumento",
                column: "AuditoriaExtraccionIaId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RevisionesIaDocumento_AuditoriaExtraccionIaId",
                table: "RevisionesIaDocumento");

            migrationBuilder.DropColumn(
                name: "AuditoriaExtraccionIaId",
                table: "RevisionesIaDocumento");
        }
    }
}
