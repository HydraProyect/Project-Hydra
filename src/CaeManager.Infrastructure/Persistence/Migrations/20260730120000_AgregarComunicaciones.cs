using System;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CaeManagerDbContext))]
    [Migration("20260730120000_AgregarComunicaciones")]
    public partial class AgregarComunicaciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversacionesCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Asunto = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Estado = table.Column<int>(type: "INTEGER", nullable: false),
                    EjecutivoAsignadoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Etiquetas = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FechaUltimoMensajeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversacionesCorreo", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MacrosRespuesta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Titulo = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    CuerpoHtml = table.Column<string>(type: "TEXT", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacrosRespuesta", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MensajesCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversacionCorreoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direccion = table.Column<int>(type: "INTEGER", nullable: false),
                    RemitenteEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CuerpoHtml = table.Column<string>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MensajesCorreo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MensajesCorreo_ConversacionesCorreo_ConversacionCorreoId",
                        column: x => x.ConversacionCorreoId,
                        principalTable: "ConversacionesCorreo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantesConversacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversacionCorreoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Rol = table.Column<int>(type: "INTEGER", nullable: false),
                    TipoOrigen = table.Column<int>(type: "INTEGER", nullable: false),
                    EntidadRelacionadaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantesConversacion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantesConversacion_ConversacionesCorreo_ConversacionCorreoId",
                        column: x => x.ConversacionCorreoId,
                        principalTable: "ConversacionesCorreo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_FechaUltimoMensajeUtc",
                table: "ConversacionesCorreo",
                column: "FechaUltimoMensajeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_Estado",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversacionesCorreo_TenantId_ClienteId",
                table: "ConversacionesCorreo",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_MacrosRespuesta_TenantId_ClienteId",
                table: "MacrosRespuesta",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_MensajesCorreo_ConversacionCorreoId",
                table: "MensajesCorreo",
                column: "ConversacionCorreoId");

            migrationBuilder.CreateIndex(
                name: "IX_MensajesCorreo_FechaUtc",
                table: "MensajesCorreo",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantesConversacion_ConversacionCorreoId",
                table: "ParticipantesConversacion",
                column: "ConversacionCorreoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParticipantesConversacion");

            migrationBuilder.DropTable(
                name: "MensajesCorreo");

            migrationBuilder.DropTable(
                name: "MacrosRespuesta");

            migrationBuilder.DropTable(
                name: "ConversacionesCorreo");
        }
    }
}
