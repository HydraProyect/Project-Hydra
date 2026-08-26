using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class F3bClienteRepunteoFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AsignacionesCartera_Clientes_PropietarioTenantId_AmbitoRela~",
                table: "AsignacionesCartera");

            migrationBuilder.DropForeignKey(
                name: "FK_AsignacionesOperacion_Clientes_PropietarioTenantId_AmbitoRe~",
                table: "AsignacionesOperacion");

            migrationBuilder.DropForeignKey(
                name: "FK_Centros_Clientes_TenantId_ClienteId",
                table: "Centros");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Clientes_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Clientes_TenantId_ClienteId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_EmpresasClientes_Clientes_TenantId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_Proyectos_Clientes_TenantId_ClienteId",
                table: "Proyectos");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Clientes_TenantId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_TarifasCliente_Clientes_TenantId_ClienteId",
                table: "TarifasCliente");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Clientes_TenantId_Id",
                table: "Clientes");

            migrationBuilder.AddForeignKey(
                name: "FK_AsignacionesCartera_Empresas_PropietarioTenantId_AmbitoRela~",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AsignacionesOperacion_Empresas_PropietarioTenantId_AmbitoRe~",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_Empresas_TenantId_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Empresas_TenantId_ClienteId",
                table: "Documentos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmpresasClientes_Empresas_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Proyectos_Empresas_TenantId_ClienteId",
                table: "Proyectos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TarifasCliente_Empresas_TenantId_ClienteId",
                table: "TarifasCliente",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AsignacionesCartera_Empresas_PropietarioTenantId_AmbitoRela~",
                table: "AsignacionesCartera");

            migrationBuilder.DropForeignKey(
                name: "FK_AsignacionesOperacion_Empresas_PropietarioTenantId_AmbitoRe~",
                table: "AsignacionesOperacion");

            migrationBuilder.DropForeignKey(
                name: "FK_Centros_Empresas_TenantId_ClienteId",
                table: "Centros");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Empresas_TenantId_ClienteId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_EmpresasClientes_Empresas_TenantId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_Proyectos_Empresas_TenantId_ClienteId",
                table: "Proyectos");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_TarifasCliente_Empresas_TenantId_ClienteId",
                table: "TarifasCliente");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Clientes_TenantId_Id",
                table: "Clientes",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_AsignacionesCartera_Clientes_PropietarioTenantId_AmbitoRela~",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AsignacionesOperacion_Clientes_PropietarioTenantId_AmbitoRe~",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_Clientes_TenantId_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Clientes_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Clientes_TenantId_ClienteId",
                table: "Documentos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmpresasClientes_Clientes_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Proyectos_Clientes_TenantId_ClienteId",
                table: "Proyectos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Clientes_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TarifasCliente_Clientes_TenantId_ClienteId",
                table: "TarifasCliente",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
