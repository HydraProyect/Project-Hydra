using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarConversacionAReclamacionDocumental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversacionId",
                table: "ReclamacionesDocumentales",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentales_ConversacionId",
                table: "ReclamacionesDocumentales",
                column: "ConversacionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReclamacionesDocumentales_Conversaciones_ConversacionId",
                table: "ReclamacionesDocumentales",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReclamacionesDocumentales_Conversaciones_ConversacionId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_ReclamacionesDocumentales_ConversacionId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropColumn(
                name: "ConversacionId",
                table: "ReclamacionesDocumentales");
        }
    }
}
