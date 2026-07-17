using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSubcontratasYTrabajadorPolimorfico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "EmpresaId",
                table: "Trabajadores",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "SubcontrataId",
                table: "Trabajadores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CredencialesAccesoSubcontrata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UrlAcceso = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CampoEmpresa = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Usuario = table.Column<string>(type: "TEXT", nullable: true),
                    Contrasena = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesAccesoSubcontrata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subcontratas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RazonSocial = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subcontratas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubcontratasClientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontratasClientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubcontratasEmpresas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontratasEmpresas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_SubcontrataId",
                table: "Trabajadores",
                column: "SubcontrataId");

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoSubcontrata_SubcontrataId",
                table: "CredencialesAccesoSubcontrata",
                column: "SubcontrataId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_RazonSocial",
                table: "Subcontratas",
                column: "RazonSocial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_ClienteId",
                table: "SubcontratasClientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_SubcontrataId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "SubcontrataId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_EmpresaId",
                table: "SubcontratasEmpresas",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "SubcontrataId", "EmpresaId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropTable(
                name: "Subcontratas");

            migrationBuilder.DropTable(
                name: "SubcontratasClientes");

            migrationBuilder.DropTable(
                name: "SubcontratasEmpresas");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_SubcontrataId",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "SubcontrataId",
                table: "Trabajadores");

            migrationBuilder.AlterColumn<Guid>(
                name: "EmpresaId",
                table: "Trabajadores",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
