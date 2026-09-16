using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class VincularRevisionIaAAuditoriaExtraccion : Migration
    {
        /// <inheritdoc />
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

        /// <inheritdoc />
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
