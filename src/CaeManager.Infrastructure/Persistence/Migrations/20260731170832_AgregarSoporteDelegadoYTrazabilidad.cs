using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSoporteDelegadoYTrazabilidad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsPlataforma",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivadaEnUtc",
                table: "DelegacionesTenant",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiraEnUtc",
                table: "DelegacionesTenant",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoActivacion",
                table: "DelegacionesTenant",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Proposito",
                table: "DelegacionesTenant",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // "Comercial" y no la cadena vacía que genera EF por defecto:
                // las delegaciones que ya existen son todas de negocio, y
                // dejarlas con un propósito ilegible haría que no encajaran en
                // ninguna de las dos categorías al leerlas.
                defaultValue: "Comercial");

            migrationBuilder.CreateTable(
                name: "RegistrosActividadSoporte",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioSoporteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DelegacionTenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Detalle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    OcurridaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosActividadSoporte", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "EsPlataforma",
                value: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosActividadSoporte_DelegacionTenantId_OcurridaEnUtc",
                table: "RegistrosActividadSoporte",
                columns: new[] { "DelegacionTenantId", "OcurridaEnUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrosActividadSoporte");

            migrationBuilder.DropColumn(
                name: "EsPlataforma",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ActivadaEnUtc",
                table: "DelegacionesTenant");

            migrationBuilder.DropColumn(
                name: "ExpiraEnUtc",
                table: "DelegacionesTenant");

            migrationBuilder.DropColumn(
                name: "MotivoActivacion",
                table: "DelegacionesTenant");

            migrationBuilder.DropColumn(
                name: "Proposito",
                table: "DelegacionesTenant");
        }
    }
}
