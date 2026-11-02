using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeRepartoAdjuntos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DestinoEmpresaId",
                table: "AdjuntosMensaje",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DestinoTrabajadorId",
                table: "AdjuntosMensaje",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentoId",
                table: "AdjuntosMensaje",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EstadoReparto",
                table: "AdjuntosMensaje",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MotivoRechazo",
                table: "AdjuntosMensaje",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResueltoEnUtc",
                table: "AdjuntosMensaje",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResueltoPorUsuarioId",
                table: "AdjuntosMensaje",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TipoDocumentoPropuestoId",
                table: "AdjuntosMensaje",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinoEmpresaId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "DestinoTrabajadorId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "DocumentoId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "EstadoReparto",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "MotivoRechazo",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "ResueltoEnUtc",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "ResueltoPorUsuarioId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropColumn(
                name: "TipoDocumentoPropuestoId",
                table: "AdjuntosMensaje");
        }
    }
}
