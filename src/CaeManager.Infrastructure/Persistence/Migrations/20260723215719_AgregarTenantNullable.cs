using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTenantNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "VisitasTrabajadores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Visitas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Vehiculos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Trabajadores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TiposDocumentoCentros",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TiposDocumento",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SubcontratasEmpresas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SubcontratasClientes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Subcontratas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "RequisitosDocumentales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "RegistrosAuditoria",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "PlataformasAcceso",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ParametrosSistema",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "NotificacionesUsuario",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "EmpresasClientes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Empresas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Documentos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DeteccionesTrabajador",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "CredencialesAccesoSubcontrata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "CredencialesAccesoEmpresa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ConfiguracionesIaDocumentoCliente",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Clientes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Centros",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Asignaciones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Alertas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "ParametrosSistema",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                column: "TenantId",
                value: null);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                column: "TenantId",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SubcontratasClientes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Subcontratas");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RequisitosDocumentales");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RegistrosAuditoria");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "PlataformasAcceso");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "NotificacionesUsuario");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "EmpresasClientes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DeteccionesTrabajador");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "CredencialesAccesoEmpresa");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ConfiguracionesIaDocumentoCliente");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Asignaciones");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Alertas");
        }
    }
}
