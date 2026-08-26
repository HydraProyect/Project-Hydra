using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class F3bSubcontrataRepunteoFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Subcontratas_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasEmpresas_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropForeignKey(
                name: "FK_Trabajadores_Subcontratas_TenantId_SubcontrataId",
                table: "Trabajadores");

            migrationBuilder.DropForeignKey(
                name: "FK_Vehiculos_Subcontratas_TenantId_SubcontrataId",
                table: "Vehiculos");

            migrationBuilder.DropForeignKey(
                name: "FK_VerificacionesExternaSubcontrata_Subcontratas_TenantId_Subc~",
                table: "VerificacionesExternaSubcontrata");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Subcontratas_TenantId_Id",
                table: "Subcontratas");

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasEmpresas_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Trabajadores_Empresas_TenantId_SubcontrataId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Vehiculos_Empresas_TenantId_SubcontrataId",
                table: "Vehiculos",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VerificacionesExternaSubcontrata_Empresas_TenantId_Subcontr~",
                table: "VerificacionesExternaSubcontrata",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasEmpresas_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropForeignKey(
                name: "FK_Trabajadores_Empresas_TenantId_SubcontrataId",
                table: "Trabajadores");

            migrationBuilder.DropForeignKey(
                name: "FK_Vehiculos_Empresas_TenantId_SubcontrataId",
                table: "Vehiculos");

            migrationBuilder.DropForeignKey(
                name: "FK_VerificacionesExternaSubcontrata_Empresas_TenantId_Subcontr~",
                table: "VerificacionesExternaSubcontrata");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Subcontratas_TenantId_Id",
                table: "Subcontratas",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Subcontratas_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId",
                principalTable: "Subcontratas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasEmpresas_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Trabajadores_Subcontratas_TenantId_SubcontrataId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Vehiculos_Subcontratas_TenantId_SubcontrataId",
                table: "Vehiculos",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VerificacionesExternaSubcontrata_Subcontratas_TenantId_Subc~",
                table: "VerificacionesExternaSubcontrata",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
