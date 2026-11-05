using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeOrigenTextoMensaje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Con DEFAULT en base: mientras el relevo cambia de ranura, la versión anterior sigue insertando mensajes sin
            // conocer la columna, y un mensaje nace siempre como escrito por una persona (0), sin sugerencia.
            migrationBuilder.AddColumn<int>(
                name: "OrigenTexto",
                table: "Mensajes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RespuestaSugeridaId",
                table: "Mensajes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Mensajes_RespuestaSugeridaId_TenantId",
                table: "Mensajes",
                columns: new[] { "RespuestaSugeridaId", "TenantId" },
                filter: "\"RespuestaSugeridaId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Mensajes_OrigenTextoSegunSugerencia",
                table: "Mensajes",
                sql: "(\"OrigenTexto\" = 0 AND \"RespuestaSugeridaId\" IS NULL) OR (\"OrigenTexto\" IN (1, 2) AND \"RespuestaSugeridaId\" IS NOT NULL)");

            // FK compuesta con el Tenant, como las de RespuestasSugeridas hacia Conversaciones y Mensajes: un mensaje solo
            // puede nombrar una sugerencia de su propio Tenant. Hace falta la clave única (Id, TenantId) que la tabla no
            // tenía. Sin ON DELETE: la aplicación nunca borra sugerencias, y el borrado de una conversación se lleva ambas
            // tablas en la misma sentencia. Sin concesiones nuevas: Mensajes ya concede SELECT a cae_app_soporte a nivel de
            // tabla y INSERT a cae_app_runtime, y la RLS de Mensajes no cambia.
            migrationBuilder.Sql("""
                ALTER TABLE public."RespuestasSugeridas"
                    ADD CONSTRAINT "UQ_RespuestasSugeridas_Id_TenantId" UNIQUE ("Id", "TenantId");

                ALTER TABLE public."Mensajes"
                    ADD CONSTRAINT "FK_Mensajes_RespuestasSugeridas_Tenant"
                        FOREIGN KEY ("RespuestaSugeridaId", "TenantId")
                        REFERENCES public."RespuestasSugeridas"("Id", "TenantId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public."Mensajes" DROP CONSTRAINT "FK_Mensajes_RespuestasSugeridas_Tenant";
                ALTER TABLE public."RespuestasSugeridas" DROP CONSTRAINT "UQ_RespuestasSugeridas_Id_TenantId";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Mensajes_RespuestaSugeridaId_TenantId",
                table: "Mensajes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Mensajes_OrigenTextoSegunSugerencia",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "OrigenTexto",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "RespuestaSugeridaId",
                table: "Mensajes");
        }
    }
}
