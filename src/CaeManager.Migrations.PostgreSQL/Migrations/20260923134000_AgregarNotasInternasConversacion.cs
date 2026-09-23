using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarNotasInternasConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotasInternasConversacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Texto = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotasInternasConversacion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotasInternasConversacion_ConversacionId",
                table: "NotasInternasConversacion",
                column: "ConversacionId");

            // FK compuesta fuera del modelo Fluent, igual que Mensajes y
            // ParticipantesConversacion (FkCompuestaTenantComunicaciones): una
            // nota no puede colgar de una conversación de otro tenant, ni
            // siquiera escribiendo SQL por debajo de EF. Referencia la clave
            // alternativa AK_Conversaciones_Id_TenantId que esa migración creó.
            migrationBuilder.Sql(
                """
                ALTER TABLE "NotasInternasConversacion" ADD CONSTRAINT "FK_NotasInternasConversacion_Conversaciones_ConversacionId_TenantId"
                FOREIGN KEY ("ConversacionId", "TenantId")
                REFERENCES "Conversaciones" ("Id", "TenantId")
                ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotasInternasConversacion");
        }
    }
}
