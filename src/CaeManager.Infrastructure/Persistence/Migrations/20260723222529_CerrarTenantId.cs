using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CerrarTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VisitasTrabajadores_VisitaId_TrabajadorId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Vehiculos_NumeroPlaca",
                table: "Vehiculos");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_Dni",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumentoCentros_TipoDocumentoId_CentroId",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumento_Nombre",
                table: "TiposDocumento");

            migrationBuilder.DropIndex(
                name: "IX_SubcontratasEmpresas_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropIndex(
                name: "IX_SubcontratasClientes_SubcontrataId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropIndex(
                name: "IX_Subcontratas_RazonSocial",
                table: "Subcontratas");

            migrationBuilder.DropIndex(
                name: "IX_PlataformasAcceso_CentroId",
                table: "PlataformasAcceso");

            migrationBuilder.DropIndex(
                name: "IX_EmpresasClientes_EmpresaId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropIndex(
                name: "IX_Empresas_Cif",
                table: "Empresas");

            migrationBuilder.DropIndex(
                name: "IX_Empresas_RazonSocial",
                table: "Empresas");

            migrationBuilder.DropIndex(
                name: "IX_CredencialesAccesoSubcontrata_SubcontrataId",
                table: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropIndex(
                name: "IX_CredencialesAccesoEmpresa_EmpresaId",
                table: "CredencialesAccesoEmpresa");

            migrationBuilder.DropIndex(
                name: "IX_ConfiguracionesIaDocumentoCliente_ClienteId_TipoDocumentoId",
                table: "ConfiguracionesIaDocumentoCliente");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_Cif",
                table: "Clientes");

            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "VisitasTrabajadores",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Visitas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Vehiculos",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Trabajadores",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "TiposDocumentoCentros",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "TiposDocumento",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "SubcontratasEmpresas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "SubcontratasClientes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Subcontratas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "RequisitosDocumentales",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "RegistrosAuditoria",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "PlataformasAcceso",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "ParametrosSistema",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "NotificacionesUsuario",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "EmpresasClientes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Empresas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Documentos",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "DeteccionesTrabajador",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "CredencialesAccesoSubcontrata",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "CredencialesAccesoEmpresa",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "ConfiguracionesIaDocumentoCliente",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Clientes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Centros",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Asignaciones",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Alertas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "ParametrosSistema",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_TenantId_VisitaId_TrabajadorId",
                table: "VisitasTrabajadores",
                columns: new[] { "TenantId", "VisitaId", "TrabajadorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_TenantId_NumeroPlaca",
                table: "Vehiculos",
                columns: new[] { "TenantId", "NumeroPlaca" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Dni" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_TenantId_TipoDocumentoId_CentroId",
                table: "TiposDocumentoCentros",
                columns: new[] { "TenantId", "TipoDocumentoId", "CentroId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumento_TenantId_Nombre",
                table: "TiposDocumento",
                columns: new[] { "TenantId", "Nombre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_TenantId_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId", "EmpresaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_TenantId_SubcontrataId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_RazonSocial",
                table: "Subcontratas",
                columns: new[] { "TenantId", "RazonSocial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlataformasAcceso_TenantId_CentroId",
                table: "PlataformasAcceso",
                columns: new[] { "TenantId", "CentroId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_TenantId_EmpresaId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "EmpresaId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_Cif",
                table: "Empresas",
                columns: new[] { "TenantId", "Cif" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_RazonSocial",
                table: "Empresas",
                columns: new[] { "TenantId", "RazonSocial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoSubcontrata_TenantId_SubcontrataId",
                table: "CredencialesAccesoSubcontrata",
                columns: new[] { "TenantId", "SubcontrataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoEmpresa_TenantId_EmpresaId",
                table: "CredencialesAccesoEmpresa",
                columns: new[] { "TenantId", "EmpresaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguracionesIaDocumentoCliente_TenantId_ClienteId_TipoDocumentoId",
                table: "ConfiguracionesIaDocumentoCliente",
                columns: new[] { "TenantId", "ClienteId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_Cif",
                table: "Clientes",
                columns: new[] { "TenantId", "Cif" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId", "CentroId", "FechaAlta" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VisitasTrabajadores_TenantId_VisitaId_TrabajadorId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Vehiculos_TenantId_NumeroPlaca",
                table: "Vehiculos");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumentoCentros_TenantId_TipoDocumentoId_CentroId",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumento_TenantId_Nombre",
                table: "TiposDocumento");

            migrationBuilder.DropIndex(
                name: "IX_SubcontratasEmpresas_TenantId_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropIndex(
                name: "IX_SubcontratasClientes_TenantId_SubcontrataId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropIndex(
                name: "IX_Subcontratas_TenantId_RazonSocial",
                table: "Subcontratas");

            migrationBuilder.DropIndex(
                name: "IX_PlataformasAcceso_TenantId_CentroId",
                table: "PlataformasAcceso");

            migrationBuilder.DropIndex(
                name: "IX_EmpresasClientes_TenantId_EmpresaId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropIndex(
                name: "IX_Empresas_TenantId_Cif",
                table: "Empresas");

            migrationBuilder.DropIndex(
                name: "IX_Empresas_TenantId_RazonSocial",
                table: "Empresas");

            migrationBuilder.DropIndex(
                name: "IX_CredencialesAccesoSubcontrata_TenantId_SubcontrataId",
                table: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropIndex(
                name: "IX_CredencialesAccesoEmpresa_TenantId_EmpresaId",
                table: "CredencialesAccesoEmpresa");

            migrationBuilder.DropIndex(
                name: "IX_ConfiguracionesIaDocumentoCliente_TenantId_ClienteId_TipoDocumentoId",
                table: "ConfiguracionesIaDocumentoCliente");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_TenantId_Cif",
                table: "Clientes");

            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "VisitasTrabajadores",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Visitas",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Vehiculos",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Trabajadores",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "TiposDocumentoCentros",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "TiposDocumento",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "SubcontratasEmpresas",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "SubcontratasClientes",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Subcontratas",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "RequisitosDocumentales",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "RegistrosAuditoria",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "PlataformasAcceso",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "ParametrosSistema",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "NotificacionesUsuario",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "EmpresasClientes",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Empresas",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Documentos",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "DeteccionesTrabajador",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "CredencialesAccesoSubcontrata",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "CredencialesAccesoEmpresa",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "ConfiguracionesIaDocumentoCliente",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Clientes",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Centros",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Asignaciones",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Alertas",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

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

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_VisitaId_TrabajadorId",
                table: "VisitasTrabajadores",
                columns: new[] { "VisitaId", "TrabajadorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_NumeroPlaca",
                table: "Vehiculos",
                column: "NumeroPlaca",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_Dni",
                table: "Trabajadores",
                column: "Dni",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_TipoDocumentoId_CentroId",
                table: "TiposDocumentoCentros",
                columns: new[] { "TipoDocumentoId", "CentroId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumento_Nombre",
                table: "TiposDocumento",
                column: "Nombre",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "SubcontrataId", "EmpresaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_SubcontrataId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "SubcontrataId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_RazonSocial",
                table: "Subcontratas",
                column: "RazonSocial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlataformasAcceso_CentroId",
                table: "PlataformasAcceso",
                column: "CentroId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_EmpresaId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "EmpresaId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_Cif",
                table: "Empresas",
                column: "Cif",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_RazonSocial",
                table: "Empresas",
                column: "RazonSocial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoSubcontrata_SubcontrataId",
                table: "CredencialesAccesoSubcontrata",
                column: "SubcontrataId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoEmpresa_EmpresaId",
                table: "CredencialesAccesoEmpresa",
                column: "EmpresaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguracionesIaDocumentoCliente_ClienteId_TipoDocumentoId",
                table: "ConfiguracionesIaDocumentoCliente",
                columns: new[] { "ClienteId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_Cif",
                table: "Clientes",
                column: "Cif",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones",
                columns: new[] { "TrabajadorId", "CentroId", "FechaAlta" },
                unique: true);
        }
    }
}
