using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeVersionPlataformaCaeClienteEmpresarial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "PlataformasCaeClienteEmpresarial",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Las filas existentes reciben un token no vacío: el comando rechaza Guid.Empty y, sin esto, su plataforma
            // no podría volver a editarse. Mismo valor aleatorio que AnadePrincipalALasCarteras (gen_random_uuid() lo
            // prohíbe IdentificadoresDeEntidadUuidV7Tests en migraciones nuevas). Después se retira el DEFAULT vacío:
            // el dominio genera la Version de cada alta, y un INSERT que la omita debe fallar en vez de quedar vacía.
            migrationBuilder.Sql("""
                UPDATE public."PlataformasCaeClienteEmpresarial"
                SET "Version" = md5(random()::text || clock_timestamp()::text || "Id"::text)::uuid;
                ALTER TABLE public."PlataformasCaeClienteEmpresarial" ALTER COLUMN "Version" DROP DEFAULT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "PlataformasCaeClienteEmpresarial");
        }
    }
}