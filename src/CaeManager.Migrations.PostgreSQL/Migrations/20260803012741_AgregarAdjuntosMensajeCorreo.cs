using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAdjuntosMensajeCorreo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdjuntosMensajeCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    NombreArchivo = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    TipoContenido = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    TamanoBytes = table.Column<long>(type: "bigint", nullable: false),
                    ArchivoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdjuntosMensajeCorreo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdjuntosMensajeCorreo_MensajesCorreo_MensajeCorreoId",
                        column: x => x.MensajeCorreoId,
                        principalTable: "MensajesCorreo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdjuntosMensajeCorreo_MensajeCorreoId",
                table: "AdjuntosMensajeCorreo",
                column: "MensajeCorreoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdjuntosMensajeCorreo");
        }
    }
}
