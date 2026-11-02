using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// <c>app_revocar_sesiones_de_cuenta</c>: revocar las sesiones de una cuenta tras un cambio de
    /// credencial (ASVS 4.0.3 V3.3.3), sin que esa cuenta tenga que firmar el contexto.
    ///
    /// <para>
    /// <c>solo_la_propia_cuenta_revoca</c> (RESTRICTIVE FOR UPDATE) acota el UPDATE a la cuenta que firma
    /// el contexto, y quien restablece la contraseña o confirma «No he sido yo» llega por un enlace de
    /// correo, sin sesión de la cuenta. El UPDATE desde la aplicación devolvería 0 filas sin error: el
    /// sello rotaría, pero las sesiones seguirían vivas y una cookie robada podría refrescarse durante el
    /// intervalo de revalidación. La política no se toca: sigue valiendo para revocar una sola sesión y
    /// para cualquier UPDATE directo. La cuenta queda probada por el token del enlace, que se comprueba
    /// en la aplicación y la base no puede ver.
    /// </para>
    ///
    /// <para>
    /// La función repite lo que sí puede comprobar: contexto firmado y Tenant firmado igual al Tenant
    /// propietario de la cuenta, de modo que un contexto de otro Tenant no revoca. No lee ni devuelve más
    /// que el número de sesiones revocadas, solo revoca (nunca reabre, por <c>sesion_revocada_no_se_reabre</c>)
    /// y solo la ejecuta <c>cae_app_runtime</c>. <c>p_conservar</c> es la sesión de quien cambia su propia
    /// contraseña; un valor que no sea de ninguna sesión las revoca todas, y <c>IS DISTINCT FROM</c> hace
    /// que un nulo también las revoque todas en vez de ninguna.
    /// </para>
    ///
    /// <para>
    /// <b>Ranura anterior:</b> la función es nueva y nada de la versión anterior la nombra.
    /// </para>
    /// </summary>
    public partial class RevocarSesionesDeCuenta : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.app_revocar_sesiones_de_cuenta(p_usuario uuid, p_conservar uuid) RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la revocación de sesiones no vería ninguna fila', current_user;
                    END IF;
                    PERFORM public.app_exigir_contexto_firmado();
                    IF (SELECT u."TenantId" FROM public."AspNetUsers" u WHERE u."Id" = p_usuario)
                        IS DISTINCT FROM public.app_ctx_tenant_id() THEN
                        RAISE EXCEPTION 'Revocación de sesiones fuera del Tenant propietario de la cuenta' USING ERRCODE = '42501';
                    END IF;
                    UPDATE public."SesionesUsuario"
                       SET "RevocadaEnUtc" = pg_catalog.now()
                     WHERE "UsuarioId" = p_usuario
                       AND "RevocadaEnUtc" IS NULL
                       AND "Sid" IS DISTINCT FROM p_conservar;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_revocar_sesiones_de_cuenta(uuid, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_revocar_sesiones_de_cuenta(uuid, uuid) TO cae_app_runtime;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION public.app_revocar_sesiones_de_cuenta(uuid, uuid);");
        }
    }
}
