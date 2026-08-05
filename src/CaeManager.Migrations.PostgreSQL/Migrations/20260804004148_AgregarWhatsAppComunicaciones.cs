using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarWhatsAppComunicaciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorEntrega",
                table: "MensajesCorreo",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EstadoEntrega",
                table: "MensajesCorreo",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Canal",
                table: "ConversacionesCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaUltimoMensajeEntranteUtc",
                table: "ConversacionesCorreo",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelefonoContacto",
                table: "ConversacionesCorreo",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Proveedor",
                table: "ConexionesIntegracion",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ContactosWhatsApp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Telefono = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactosWhatsApp", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LineasWhatsApp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionIntegracionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhoneNumberId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WabaId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NumeroTelefono = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TokenAcceso = table.Column<string>(type: "text", nullable: false),
                    Modo = table.Column<int>(type: "integer", nullable: false),
                    ComercialAsignadoId = table.Column<Guid>(type: "uuid", nullable: true),
                    MensajeAutoTriage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineasWhatsApp", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LineasWhatsApp_ConexionesIntegracion_ConexionIntegracionId",
                        column: x => x.ConexionIntegracionId,
                        principalTable: "ConexionesIntegracion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MiembrosPoolLinea",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineaWhatsAppId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MiembrosPoolLinea", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MiembrosPoolLinea_LineasWhatsApp_LineaWhatsAppId",
                        column: x => x.LineaWhatsAppId,
                        principalTable: "LineasWhatsApp",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_Canal_Estado",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "Canal", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_ConexionIntegracionId_Telefon~",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "ConexionIntegracionId", "TelefonoContacto" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosWhatsApp_TenantId_Telefono",
                table: "ContactosWhatsApp",
                columns: new[] { "TenantId", "Telefono" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LineasWhatsApp_ConexionIntegracionId",
                table: "LineasWhatsApp",
                column: "ConexionIntegracionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LineasWhatsApp_PhoneNumberId",
                table: "LineasWhatsApp",
                column: "PhoneNumberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MiembrosPoolLinea_LineaWhatsAppId_UsuarioId",
                table: "MiembrosPoolLinea",
                columns: new[] { "LineaWhatsAppId", "UsuarioId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactosWhatsApp");

            migrationBuilder.DropTable(
                name: "MiembrosPoolLinea");

            migrationBuilder.DropTable(
                name: "LineasWhatsApp");

            migrationBuilder.DropIndex(
                name: "IX_ConversacionesCorreo_TenantId_Canal_Estado",
                table: "ConversacionesCorreo");

            migrationBuilder.DropIndex(
                name: "IX_ConversacionesCorreo_TenantId_ConexionIntegracionId_Telefon~",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "ErrorEntrega",
                table: "MensajesCorreo");

            migrationBuilder.DropColumn(
                name: "EstadoEntrega",
                table: "MensajesCorreo");

            migrationBuilder.DropColumn(
                name: "Canal",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "FechaUltimoMensajeEntranteUtc",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "TelefonoContacto",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "Proveedor",
                table: "ConexionesIntegracion");
        }
    }
}
