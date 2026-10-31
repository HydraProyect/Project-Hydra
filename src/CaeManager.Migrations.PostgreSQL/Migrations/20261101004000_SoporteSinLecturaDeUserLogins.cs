using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG deja de leer <c>AspNetUserLogins</c> (OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio): la tabla
    /// guarda <c>ProviderKey</c>, el identificador de la persona en el proveedor externo de acceso, y no tiene RLS,
    /// así que el <c>SELECT</c> de tabla de <c>cae_app_soporte</c> exponía los vínculos de todos los Tenants. Una
    /// concesión de columnas no lo arreglaba: sin RLS, <c>UserId</c> seguiría siendo transversal.
    ///
    /// <para>
    /// <b>La tabla sigue sin RLS.</b> Esta migración no la cambia: quien la protege frente a Soporte TALVEG es la
    /// ausencia de privilegio. <c>cae_app_runtime</c> conserva su acceso porque el inicio de sesión resuelve el
    /// vínculo antes de conocer el Tenant, y no hay contexto firmado que pudiera acotarlo.
    /// </para>
    ///
    /// <para>
    /// <c>app_cuenta_tiene_login_externo</c> responde solo con un booleano. El Tenant no es un parámetro: sale de
    /// <c>app_ctx_tenant_id()</c>, del token firmado, y una cuenta de otro Tenant responde lo mismo que una que no
    /// existe o que no tiene vínculo, de modo que la función no es un oráculo entre Tenants. Sin contexto firmado
    /// responde <c>false</c>; con un contexto falsificado, <c>app_contexto_validado</c> lanza <c>42501</c>. Tampoco
    /// responde sobre las cuentas de otro Tenant que la RLS de <c>AspNetUsers</c> sí deja ver, como la del Actor real
    /// o la de un Operador CAE externo delegado.
    /// </para>
    ///
    /// <para>
    /// Como <c>app_cuenta_firmada_vigente</c>, su propietario tiene que evitar la RLS: si no, respondería
    /// <c>false</c> a toda cuenta, y lo comprueba en vez de callar. <c>EXECUTE</c> solo para
    /// <c>cae_app_soporte</c>. <c>app_conceder_administrador_por_plataforma</c>, que también consulta la tabla, es
    /// <c>SECURITY DEFINER</c>: lee con su propietario y no depende del privilegio que aquí se retira.
    /// </para>
    ///
    /// <para>
    /// La función nace antes del <c>REVOKE</c>, y todo va en la misma transacción. El privilegio se comprueba al
    /// ejecutar cada consulta, de modo que la versión anterior de la aplicación que siga en marcha durante el relevo
    /// recibe <c>42501</c> en esta lectura si la hace bajo Sesión Privilegiada: falla cerrada, y solo hasta el relevo.
    /// </para>
    /// </summary>
    public partial class SoporteSinLecturaDeUserLogins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.app_cuenta_tiene_login_externo(p_usuario uuid) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de la cuenta no vería ninguna fila', current_user;
                    END IF;
                    RETURN EXISTS (SELECT 1 FROM public."AspNetUsers" u
                                    JOIN public."AspNetUserLogins" l ON l."UserId" = u."Id"
                                    WHERE u."Id" = p_usuario AND u."TenantId" = public.app_ctx_tenant_id());
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_cuenta_tiene_login_externo(uuid) FROM PUBLIC, cae_app_runtime, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_cuenta_tiene_login_externo(uuid) TO cae_app_soporte;
                REVOKE ALL ON TABLE public."AspNetUserLogins" FROM cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT SELECT ON TABLE public."AspNetUserLogins" TO cae_app_soporte;
                DROP FUNCTION public.app_cuenta_tiene_login_externo(uuid);
                """);
        }
    }
}
