using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeFichaEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanalFirmaDocumentos",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoFormacionCorreo",
                table: "Empresas",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoFormacionNombre",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoFormacionTelefono",
                table: "Empresas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoSpaCorreo",
                table: "Empresas",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoSpaNombre",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoSpaTelefono",
                table: "Empresas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirmanteDocumentos",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModalidadOrganizacionPreventiva",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mutua",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizacionSpa",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SelloCustodiadoPorOperadorCae",
                table: "Empresas",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TecnicoPrlCorreo",
                table: "Empresas",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TecnicoPrlNombre",
                table: "Empresas",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TecnicoPrlTelefono",
                table: "Empresas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanalFirmaDocumentos",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ContactoFormacionCorreo",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ContactoFormacionNombre",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ContactoFormacionTelefono",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ContactoSpaCorreo",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ContactoSpaNombre",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ContactoSpaTelefono",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "FirmanteDocumentos",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "ModalidadOrganizacionPreventiva",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "Mutua",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "OrganizacionSpa",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "SelloCustodiadoPorOperadorCae",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "TecnicoPrlCorreo",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "TecnicoPrlNombre",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "TecnicoPrlTelefono",
                table: "Empresas");
        }
    }
}
