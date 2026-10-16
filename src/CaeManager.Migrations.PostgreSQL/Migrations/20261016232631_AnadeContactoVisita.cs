using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeContactoVisita : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactoCorreo",
                table: "Visitas",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoNombre",
                table: "Visitas",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoTelefono",
                table: "Visitas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // El contacto de la Visita es, por defecto, el del Centro: las Visitas existentes no canceladas lo reciben
            // con el mismo criterio que Visita.RellenarContactoDesdeCentro (texto recortado al nombre, o al correo si
            // el texto entero es un correo válido según Visita.EsCorreoDeContactoValido). Solo Visitas sin contacto, y
            // siempre con el Centro del mismo Tenant.
            migrationBuilder.Sql("""
                UPDATE public."Visitas" AS v
                SET "ContactoNombre" = CASE WHEN c.es_correo THEN NULL ELSE c.contacto END,
                    "ContactoCorreo" = CASE WHEN c.es_correo THEN c.contacto ELSE NULL END
                FROM (
                    SELECT t."Id", t."TenantId", t.contacto,
                           (t.contacto ~ '^[^@[:space:]]+@[^@[:space:]]+$' AND length(t.contacto) <= 254) AS es_correo
                    FROM (
                        SELECT "Id", "TenantId", regexp_replace("Contacto", '^[[:space:]]+|[[:space:]]+$', '', 'g') AS contacto
                        FROM public."Centros"
                        WHERE "Contacto" IS NOT NULL
                    ) AS t
                    WHERE t.contacto <> ''
                ) AS c
                WHERE v."CentroId" = c."Id" AND v."TenantId" = c."TenantId"
                  AND NOT v."EstaCancelada" AND NOT v."EstaEliminado"
                  AND v."ContactoNombre" IS NULL AND v."ContactoTelefono" IS NULL AND v."ContactoCorreo" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactoCorreo",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "ContactoNombre",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "ContactoTelefono",
                table: "Visitas");
        }
    }
}
