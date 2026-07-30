using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarDelegacionTenantYAsignacionOperadorDelegado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AsignacionesOperadorDelegado",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DelegacionTenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rol = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsignacionesOperadorDelegado", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DelegacionesTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantConsultoraId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelegacionesTenant", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
                table: "AsignacionesOperadorDelegado",
                columns: new[] { "DelegacionTenantId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperadorDelegado_UsuarioId",
                table: "AsignacionesOperadorDelegado",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_DelegacionesTenant_TenantConsultoraId_TenantClienteId_Activa",
                table: "DelegacionesTenant",
                columns: new[] { "TenantConsultoraId", "TenantClienteId", "Activa" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsignacionesOperadorDelegado");

            migrationBuilder.DropTable(
                name: "DelegacionesTenant");
        }
    }
}
