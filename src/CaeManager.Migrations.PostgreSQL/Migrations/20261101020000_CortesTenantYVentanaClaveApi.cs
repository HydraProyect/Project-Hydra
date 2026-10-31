using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Cortes de las claves de la API pública (SEG-8; OWASP ASVS 4.0.3 V4.1.3 y V4.1.5).
    /// <c>Tenants.ClavesApiValidasDesdeUtc</c> guarda la última reactivación del Tenant: reactivar no
    /// devuelve las claves emitidas antes. <c>ClavesApi.VentanaSoporteId</c> y
    /// <c>VentanaSoporteAbiertaEnUtc</c> guardan la ventana de Soporte TALVEG en que se emitió cada
    /// clave. Las claves anteriores quedan sin ventana y dejan de autenticar: como mucho habrían
    /// vivido lo que quedara de una ventana de 4 horas.
    ///
    /// <para>
    /// <c>cae_app_soporte</c>, que lee <c>ClavesApi</c> por columnas, recibe esas dos (no son
    /// secretas) y <c>EXECUTE</c> de <c>app_estado_cuenta_emisora</c>: el listado del Actor de
    /// Plataforma TALVEG evalúa el mismo respaldo que la autenticación y, en Sesión Privilegiada,
    /// corre con ese rol. Sin el permiso, la pantalla fallaría con 42501. La función responde por una
    /// cuenta concreta sin leer credenciales.
    /// </para>
    /// </summary>
    public partial class CortesTenantYVentanaClaveApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClavesApiValidasDesdeUtc",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VentanaSoporteAbiertaEnUtc",
                table: "ClavesApi",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VentanaSoporteId",
                table: "ClavesApi",
                type: "uuid",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "ClavesApiValidasDesdeUtc",
                value: null);

            migrationBuilder.Sql("""
                GRANT SELECT ("VentanaSoporteId", "VentanaSoporteAbiertaEnUtc") ON "ClavesApi" TO cae_app_soporte;
                GRANT EXECUTE ON FUNCTION public.app_estado_cuenta_emisora(uuid) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE EXECUTE ON FUNCTION public.app_estado_cuenta_emisora(uuid) FROM cae_app_soporte;
                """);

            migrationBuilder.DropColumn(
                name: "VentanaSoporteAbiertaEnUtc",
                table: "ClavesApi");

            migrationBuilder.DropColumn(
                name: "VentanaSoporteId",
                table: "ClavesApi");

            migrationBuilder.DropColumn(
                name: "ClavesApiValidasDesdeUtc",
                table: "Tenants");
        }
    }
}
