using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Desactivar la cuenta emisora invalida sus claves de API (decisión del 2026-10-28; OWASP ASVS
    /// 4.0.3 V4.1.3 y V4.1.5). <c>UltimaDesactivacionEnUtc</c> sobrevive a la reactivación, que es lo que
    /// impide que una clave anterior vuelva a autenticar. El relleno se la da a las cuentas ya
    /// desactivadas (mismo umbral que <c>ApplicationUser.UmbralDeCuentaDesactivada</c>). De las que se
    /// reactivaron antes de esta migración no queda rastro.
    ///
    /// <para>
    /// La cuenta emisora es de otra organización que el Tenant propietario de la clave, y la autenticación
    /// corre bajo el ámbito de ese Tenant: la política de <c>AspNetUsers</c> no le deja ver la fila. Mismo
    /// patrón que <c>app_tenant_de_cuenta</c>, sin ampliar ninguna política: <c>app_estado_cuenta_emisora</c>
    /// es SECURITY DEFINER, solo la ejecuta <c>cae_app_runtime</c> y devuelve dos fechas de esa cuenta,
    /// no su fila. Sin la cuenta no devuelve filas, y el llamador lo trata como clave no respaldada.
    /// </para>
    /// </summary>
    public partial class CuentaEmisoraCortaClaveApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UltimaDesactivacionEnUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE public."AspNetUsers" SET "UltimaDesactivacionEnUtc" = now()
                  WHERE "LockoutEnd" > now() + interval '365 days';
                CREATE FUNCTION public.app_estado_cuenta_emisora(cuenta_id uuid)
                    RETURNS TABLE(fin_bloqueo timestamp with time zone, ultima_desactivacion timestamp with time zone)
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT u."LockoutEnd", u."UltimaDesactivacionEnUtc" FROM public."AspNetUsers" u WHERE u."Id" = cuenta_id;
                $$;
                REVOKE ALL ON FUNCTION public.app_estado_cuenta_emisora(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_estado_cuenta_emisora(uuid) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.app_estado_cuenta_emisora(uuid);");

            migrationBuilder.DropColumn(
                name: "UltimaDesactivacionEnUtc",
                table: "AspNetUsers");
        }
    }
}
