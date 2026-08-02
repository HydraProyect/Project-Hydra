using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class CrearClaveApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClavesApi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NombreDescriptivo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PrefijoVisible = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HashClave = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UltimoUsoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClavesApi", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClavesApi_HashClave",
                table: "ClavesApi",
                column: "HashClave",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClavesApi_TenantId",
                table: "ClavesApi",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClavesApi");
        }
    }
}
