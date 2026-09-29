using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class LogoTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LogoActualizadoEnUtc",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoArchivoClave",
                table: "Tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoVersion",
                table: "Tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Soporte TALVEG escribe el logo con la capacidad Aprovisionamiento (decisión 4 del
            // contrato del selector de Tenant). Excepción declarada a la exclusión de "Tenants" del
            // rol de escritura acotada: UPDATE solo de las tres columnas del logo, nunca de tabla.
            // El SELECT de tabla es el mínimo que exige cargar la entidad rastreada con EF; no amplía
            // lo que ese mismo actor ya lee, porque cae_app_soporte tiene SELECT sobre "Tenants".
            // Sin INSERT ni DELETE.
            migrationBuilder.Sql(
                """
                GRANT SELECT ON TABLE public."Tenants" TO cae_app_aprovisionamiento;
                GRANT UPDATE ("LogoArchivoClave", "LogoVersion", "LogoActualizadoEnUtc") ON TABLE public."Tenants" TO cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE UPDATE ("LogoArchivoClave", "LogoVersion", "LogoActualizadoEnUtc") ON TABLE public."Tenants" FROM cae_app_aprovisionamiento;
                REVOKE SELECT ON TABLE public."Tenants" FROM cae_app_aprovisionamiento;
                """);

            migrationBuilder.DropColumn(
                name: "LogoActualizadoEnUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogoArchivoClave",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogoVersion",
                table: "Tenants");
        }
    }
}
