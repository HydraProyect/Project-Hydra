using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class CrearIntegracionesMicrosoft365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MensajeExternoId",
                table: "MensajesCorreo",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConexionIntegracionId",
                table: "ConversacionesCorreo",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HiloExternoId",
                table: "ConversacionesCorreo",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConexionesIntegracion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    BuzonEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    UltimoError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FechaConectadaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConexionesIntegracion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CredencialesIntegracion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionIntegracionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshToken = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesIntegracion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventosWebhook",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionIntegracionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadCrudo = table.Column<string>(type: "text", nullable: false),
                    Procesado = table.Column<bool>(type: "boolean", nullable: false),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    ErrorProcesado = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FechaRecepcionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventosWebhook", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SuscripcionesWebhook",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionIntegracionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GraphSubscriptionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientState = table.Column<string>(type: "text", nullable: false),
                    FechaExpiracionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuscripcionesWebhook", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MensajesCorreo_TenantId_MensajeExternoId",
                table: "MensajesCorreo",
                columns: new[] { "TenantId", "MensajeExternoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_HiloExternoId",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "HiloExternoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConexionesIntegracion_TenantId_ClienteId",
                table: "ConexionesIntegracion",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConexionesIntegracion_TenantId_Nombre",
                table: "ConexionesIntegracion",
                columns: new[] { "TenantId", "Nombre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesIntegracion_TenantId_ConexionIntegracionId",
                table: "CredencialesIntegracion",
                columns: new[] { "TenantId", "ConexionIntegracionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_ConexionIntegracionId",
                table: "EventosWebhook",
                column: "ConexionIntegracionId");

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_TenantId_Procesado",
                table: "EventosWebhook",
                columns: new[] { "TenantId", "Procesado" });

            migrationBuilder.CreateIndex(
                name: "IX_SuscripcionesWebhook_FechaExpiracionUtc",
                table: "SuscripcionesWebhook",
                column: "FechaExpiracionUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SuscripcionesWebhook_TenantId_ConexionIntegracionId",
                table: "SuscripcionesWebhook",
                columns: new[] { "TenantId", "ConexionIntegracionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConexionesIntegracion");

            migrationBuilder.DropTable(
                name: "CredencialesIntegracion");

            migrationBuilder.DropTable(
                name: "EventosWebhook");

            migrationBuilder.DropTable(
                name: "SuscripcionesWebhook");

            migrationBuilder.DropIndex(
                name: "IX_MensajesCorreo_TenantId_MensajeExternoId",
                table: "MensajesCorreo");

            migrationBuilder.DropIndex(
                name: "IX_ConversacionesCorreo_TenantId_HiloExternoId",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "MensajeExternoId",
                table: "MensajesCorreo");

            migrationBuilder.DropColumn(
                name: "ConexionIntegracionId",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "HiloExternoId",
                table: "ConversacionesCorreo");
        }
    }
}
