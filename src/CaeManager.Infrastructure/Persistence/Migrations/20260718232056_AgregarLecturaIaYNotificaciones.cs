using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarLecturaIaYNotificaciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true (no false) — cualquier TipoDocumento ya existente en una
            // base de datos real (no solo los 32 del seed, también los que un
            // Administrador haya creado desde /tipos-documento) debe arrancar con la
            // lectura IA activa, igual que el default del propio dominio
            // (TipoDocumento.LecturaIaActiva = true) — nunca desactivada por sorpresa
            // tras aplicar esta migración.
            migrationBuilder.AddColumn<bool>(
                name: "LecturaIaActiva",
                table: "TiposDocumento",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ConfiguracionesIaDocumentoCliente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracionesIaDocumentoCliente", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificacionesUsuario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioDestinatarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Titulo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Mensaje = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Leida = table.Column<bool>(type: "INTEGER", nullable: false),
                    UrlAccion = table.Column<string>(type: "TEXT", nullable: true),
                    TextoAccion = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificacionesUsuario", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                column: "LecturaIaActiva",
                value: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguracionesIaDocumentoCliente_ClienteId_TipoDocumentoId",
                table: "ConfiguracionesIaDocumentoCliente",
                columns: new[] { "ClienteId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificacionesUsuario_UsuarioDestinatarioId_Leida",
                table: "NotificacionesUsuario",
                columns: new[] { "UsuarioDestinatarioId", "Leida" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfiguracionesIaDocumentoCliente");

            migrationBuilder.DropTable(
                name: "NotificacionesUsuario");

            migrationBuilder.DropColumn(
                name: "LecturaIaActiva",
                table: "TiposDocumento");
        }
    }
}
