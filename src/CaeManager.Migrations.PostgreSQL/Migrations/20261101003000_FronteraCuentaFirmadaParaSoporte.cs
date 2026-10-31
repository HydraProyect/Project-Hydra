using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Frontera de autenticación para Soporte TALVEG (OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio): la revalidación
    /// de un circuito en Sesión Privilegiada ya no necesita leer <c>AspNetUsers.SecurityStamp</c> con
    /// <c>cae_app_soporte</c>. <c>app_cuenta_firmada_vigente</c> responde solo con un booleano si el sello
    /// recibido es el de la cuenta que firma el contexto y esa cuenta sigue activa.
    ///
    /// <para>
    /// <b>La cuenta no es un parámetro.</b> Sale de <c>app_ctx_usuario_id()</c>, del token firmado, de modo que
    /// la función no sabe preguntar por otra cuenta y no sirve de oráculo sobre el sello ajeno. Sin contexto
    /// firmado, sin sello o sin cuenta, responde <c>false</c>. No devuelve el sello ni el hash.
    /// </para>
    ///
    /// <para>
    /// <b>Actor real.</b> <c>app_ctx_usuario_id()</c> es la cuenta del principal de la sesión. Ninguna Sesión
    /// Privilegiada simula hoy a nadie (<c>AbrirSesionPrivilegiadaCommand</c> abre con
    /// <c>usuarioSimuladoId: null</c>), así que esa cuenta es la del Actor real. Si algún día el contexto firmara
    /// al Usuario simulado, el sello de la cookie, que es el del Actor real, dejaría de coincidir y la
    /// revalidación fallaría cerrada, nunca por la cuenta equivocada.
    /// </para>
    ///
    /// <para>
    /// Mismo umbral que <c>ApplicationUser.UmbralDeCuentaDesactivada</c>, como
    /// <c>app_conceder_administrador_por_plataforma</c>: un bloqueo temporal por intentos fallidos no corta la
    /// sesión, y el de una cuenta desactivada sí. <c>FronteraCuentaFirmadaTests</c> lo ata al umbral.
    /// </para>
    ///
    /// <para>
    /// Como <c>app_sesion_vigente</c>, su propietario tiene que evitar la RLS: si no, respondería <c>false</c> a
    /// toda sesión, y lo comprueba en vez de callar. <c>EXECUTE</c> solo para <c>cae_app_soporte</c>: fuera de una
    /// Sesión Privilegiada la validación no pasa por aquí. No concede ni retira privilegios de tabla: el
    /// <c>REVOKE</c> de las columnas es un incremento aparte.
    /// </para>
    /// </summary>
    public partial class FronteraCuentaFirmadaParaSoporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.app_cuenta_firmada_vigente(p_sello text) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de la cuenta no vería ninguna fila', current_user;
                    END IF;
                    RETURN EXISTS (SELECT 1 FROM public."AspNetUsers" u
                                    WHERE u."Id" = public.app_ctx_usuario_id()
                                      AND u."SecurityStamp" = p_sello
                                      AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= pg_catalog.now() + interval '365 days'));
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_cuenta_firmada_vigente(text) FROM PUBLIC, cae_app_runtime, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_cuenta_firmada_vigente(text) TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION public.app_cuenta_firmada_vigente(text);
                """);
        }
    }
}
