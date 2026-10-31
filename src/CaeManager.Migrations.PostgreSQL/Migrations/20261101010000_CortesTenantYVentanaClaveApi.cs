using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Cortes de las claves de la API pública (SEG-8; OWASP ASVS 4.0.3 V4.1.3 y V4.1.5).
    /// <c>Tenants.ClavesApiValidasDesdeUtc</c> guarda la última reactivación del Tenant: reactivar no
    /// devuelve las claves emitidas antes.
    ///
    /// <para>
    /// <c>EXECUTE</c> de <c>app_tenant_de_cuenta</c> y <c>app_estado_cuenta_emisora</c> para
    /// <c>cae_app_soporte</c>: el listado del Actor de Plataforma TALVEG evalúa el mismo respaldo que
    /// la autenticación y, en Sesión Privilegiada, corre con ese rol. Sin el permiso, la pantalla
    /// fallaría con 42501. Ambas responden por una cuenta concreta sin leer credenciales.
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

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "ClavesApiValidasDesdeUtc",
                value: null);

            migrationBuilder.Sql("""
                GRANT EXECUTE ON FUNCTION public.app_tenant_de_cuenta(uuid) TO cae_app_soporte;
                GRANT EXECUTE ON FUNCTION public.app_estado_cuenta_emisora(uuid) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE EXECUTE ON FUNCTION public.app_estado_cuenta_emisora(uuid) FROM cae_app_soporte;
                REVOKE EXECUTE ON FUNCTION public.app_tenant_de_cuenta(uuid) FROM cae_app_soporte;
                """);

            migrationBuilder.DropColumn(
                name: "ClavesApiValidasDesdeUtc",
                table: "Tenants");
        }
    }
}
