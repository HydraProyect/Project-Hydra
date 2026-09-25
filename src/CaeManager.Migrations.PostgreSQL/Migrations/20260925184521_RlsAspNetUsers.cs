using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// <b>P1-M1: RLS en <c>AspNetUsers</c>.</b> Hasta aquí la tabla de cuentas quedaba
    /// fuera de <c>HabilitarRlsPostgres</c> a propósito, y bajo <c>cae_app_runtime</c>
    /// cualquier consulta veía las cuentas de todos los Tenants: la guarda en C# de cada
    /// lector era la única barrera (medido por P0-8, #896, con el autorizador del
    /// restablecimiento de la 2FA).
    ///
    /// <para>
    /// <b>Sin <c>FORCE</c>, a propósito</b>, como los catálogos de asignación
    /// (<c>RlsCatalogosDeAsignacion</c>): la identidad administrativa del arranque
    /// (<c>FabricaContextoDeBootstrap</c>: el backfill de asignaciones operativas y la
    /// retirada de un Tenant de demostración) conecta como propietario y necesita el
    /// grafo completo. El tráfico conecta como <c>cae_app_runtime</c>, que no es
    /// propietario: la política le aplica siempre.
    /// </para>
    ///
    /// <para>
    /// <b>Lectura (SELECT)</b>, por cualquiera de estas relaciones con el contexto:
    /// </para>
    /// <list type="bullet">
    /// <item>A — la cuenta es del Tenant activo (<c>app.tenant_id</c>).</item>
    /// <item>B — es la propia cuenta (<c>app.usuario_id</c>): un Gestor CAE, un
    /// Operador Delegado o Soporte TALVEG leen su propia fila aunque operen en otro
    /// Tenant (cookie, layout, tema, idioma, 2FA).</item>
    /// <item>D — es del Tenant de origen de quien mira (<c>app.tenant_origen_id</c>):
    /// su propia organización, que ya gobierna cuando opera desde ella.</item>
    /// <item>E1 — es o ha sido Operador Delegado del Tenant activo (una
    /// <c>AsignacionOperadorDelegado</c> en una delegación cuyo Tenant cliente es el
    /// activo, vigente o no): <c>/delegaciones</c> nombra también a los operadores de
    /// delegaciones ya desactivadas o caducadas. Más ancha que
    /// <c>DirectorioUsuariosTenant.ObtenerRolesDeOperadoresDelegadosAsync</c>, que
    /// sigue decidiendo, en C#, quién aparece en las listas y con qué rol: la política
    /// es el límite exterior, no la regla de negocio.</item>
    /// <item>E2 — tiene una Asignación de Cartera vigente sobre el Tenant activo, bajo
    /// una Asignación de Operación vigente de su propio Operador CAE: la misma regla
    /// que <c>DirectorioUsuariosTenant.ObtenerCarterasVigentesAsync</c>.</item>
    /// <item>G — aparece como actor (<c>UsuarioId</c> o <c>ActorRealUsuarioId</c>) en
    /// la auditoría del Tenant activo: quien actuó sobre los datos de un Tenant es
    /// nombrable en ese Tenant (Soporte TALVEG, un Operador que ya no lo es). Lo
    /// deriva un dato del propio Tenant, bajo la RLS de <c>RegistrosAuditoria</c>, y
    /// lo sirven los índices <c>(TenantId, UsuarioId, …)</c> y
    /// <c>(TenantId, ActorRealUsuarioId, …)</c>.</item>
    /// <item>G2 — aparece como actor en el registro de accesos a documentos sensibles
    /// del Tenant activo (pantalla <c>AccesosDocumentosSensibles</c>).</item>
    /// <item>G3 — es el usuario de Soporte TALVEG de una actividad de soporte
    /// registrada en el Tenant activo: el Tenant visitado ve quién lo visitó.</item>
    /// </list>
    /// <para>
    /// Las subconsultas se evalúan con los privilegios y la RLS de quien consulta,
    /// no del propietario: no amplían lo que <c>cae_app_runtime</c> o
    /// <c>cae_app_soporte</c> ven de esas tablas.
    /// </para>
    ///
    /// <para>
    /// <b>Escritura, más estrecha que la lectura.</b> Alta y baja solo en el Tenant
    /// activo (A). Modificación: A, o la propia cuenta (B); y la propia cuenta, por
    /// cualquiera de las dos ramas, solo si sigue en su Tenant de origen: quien opera
    /// otro Tenant no puede trasladarse a él. D, E1, E2 y G no dan escritura: poder
    /// nombrar a un actor no es poder cambiar su contraseña, su 2FA o su sello de
    /// seguridad.
    /// </para>
    ///
    /// <para>
    /// <b>Antes de que exista Tenant</b> (login, 2FA, recuperación de contraseña, SSO,
    /// validación del sello de la cookie, token de la extensión) no hay contexto que
    /// case con ninguna rama, y la política falla cerrada. Esos caminos resuelven el
    /// Tenant de la cuenta con las funciones de abajo —el mismo patrón que
    /// <c>app_tenant_de_clave_api</c>— y leen la fila dentro de
    /// <c>AmbitoTenantExplicito</c>, bajo la política de su propio Tenant
    /// (<c>AlmacenUsuarios</c>, solo dentro de <c>AmbitoIdentificacionSinTenant</c>).
    /// Devuelven el <c>TenantId</c> (y el <c>Id</c>), nunca la fila: lo que se aprende
    /// llamándolas es que existe una cuenta con esa clave y de qué Tenant es, lo mismo
    /// que el validador de Identity ya revela al rechazar un correo duplicado. Las usa
    /// también <c>ValidadorUnicidadGlobalCuenta</c>: el nombre y el correo siguen
    /// siendo únicos entre Tenants aunque la otra cuenta no sea visible.
    /// </para>
    ///
    /// <para>
    /// <c>search_path</c> fijado a <c>pg_catalog, pg_temp</c> y relación cualificada,
    /// por el mismo motivo que <c>ResolucionDeClaveApiBajoRls</c>. <c>EXECUTE</c> solo
    /// para <c>cae_app_runtime</c>: es el único rol que autentica tráfico y da de alta
    /// cuentas (<c>cae_app_aprovisionamiento</c> no tiene privilegio alguno sobre
    /// <c>AspNetUsers</c>; <c>cae_app_soporte</c> es de solo lectura).
    /// </para>
    ///
    /// <para>
    /// Pura DDL de servidor: no cambia el modelo de EF.
    /// </para>
    /// </summary>
    public partial class RlsAspNetUsers : Migration
    {
        private const string Tenant = @"NULLIF(current_setting('app.tenant_id', true), '')::uuid";
        private const string TenantOrigen = @"NULLIF(current_setting('app.tenant_origen_id', true), '')::uuid";
        private const string Usuario = @"NULLIF(current_setting('app.usuario_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION app_tenant_de_cuenta(cuenta_id uuid) RETURNS uuid
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT u.""TenantId"" FROM public.""AspNetUsers"" u WHERE u.""Id"" = cuenta_id;
$$;
REVOKE ALL ON FUNCTION app_tenant_de_cuenta(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_tenant_de_cuenta(uuid) TO cae_app_runtime;

CREATE FUNCTION app_cuenta_por_nombre_normalizado(nombre_normalizado text)
  RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT u.""Id"", u.""TenantId"" FROM public.""AspNetUsers"" u
    WHERE u.""NormalizedUserName"" = nombre_normalizado;
$$;
REVOKE ALL ON FUNCTION app_cuenta_por_nombre_normalizado(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_cuenta_por_nombre_normalizado(text) TO cae_app_runtime;

CREATE FUNCTION app_cuentas_por_email_normalizado(email_normalizado text)
  RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT u.""Id"", u.""TenantId"" FROM public.""AspNetUsers"" u
    WHERE u.""NormalizedEmail"" = email_normalizado;
$$;
REVOKE ALL ON FUNCTION app_cuentas_por_email_normalizado(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_cuentas_por_email_normalizado(text) TO cae_app_runtime;
");

            migrationBuilder.Sql($@"
ALTER TABLE ""AspNetUsers"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""AspNetUsers"" NO FORCE ROW LEVEL SECURITY;

CREATE POLICY cuentas_lectura ON ""AspNetUsers"" FOR SELECT
  USING (
        ""TenantId"" = {Tenant}
     OR ""Id"" = {Usuario}
     OR ""TenantId"" = {TenantOrigen}
     OR EXISTS (
          SELECT 1 FROM ""AsignacionesOperadorDelegado"" a
          JOIN ""DelegacionesTenant"" d ON d.""Id"" = a.""DelegacionTenantId""
          WHERE a.""UsuarioId"" = ""AspNetUsers"".""Id""
            AND d.""TenantClienteId"" = {Tenant})
     OR EXISTS (
          SELECT 1 FROM ""AsignacionesCartera"" c
          JOIN ""AsignacionesOperacion"" o ON o.""Id"" = c.""AsignacionOperacionId""
          WHERE c.""UsuarioId"" = ""AspNetUsers"".""Id""
            AND c.""PropietarioTenantId"" = {Tenant}
            AND c.""Estado"" = 'Vigente' AND c.""VigenciaDesde"" <= now()
            AND (c.""VigenciaHasta"" IS NULL OR now() < c.""VigenciaHasta"")
            AND o.""Estado"" = 'Vigente' AND o.""VigenciaDesde"" <= now()
            AND (o.""VigenciaHasta"" IS NULL OR now() < o.""VigenciaHasta"")
            AND o.""OperadorTenantId"" = ""AspNetUsers"".""TenantId"")
     OR EXISTS (
          SELECT 1 FROM ""RegistrosAuditoria"" r
          WHERE r.""TenantId"" = {Tenant} AND r.""UsuarioId"" = ""AspNetUsers"".""Id"")
     OR EXISTS (
          SELECT 1 FROM ""RegistrosAuditoria"" r
          WHERE r.""TenantId"" = {Tenant} AND r.""ActorRealUsuarioId"" = ""AspNetUsers"".""Id"")
     OR EXISTS (
          SELECT 1 FROM ""RegistrosAccesoDocumentoSensible"" s
          WHERE s.""TenantId"" = {Tenant}
            AND (s.""UsuarioId"" = ""AspNetUsers"".""Id"" OR s.""ActorRealUsuarioId"" = ""AspNetUsers"".""Id""))
     OR EXISTS (
          SELECT 1 FROM ""RegistrosActividadSoporte"" v
          WHERE v.""TenantId"" = {Tenant} AND v.""UsuarioSoporteId"" = ""AspNetUsers"".""Id""));

CREATE POLICY cuentas_alta ON ""AspNetUsers"" FOR INSERT
  WITH CHECK (""TenantId"" = {Tenant});

CREATE POLICY cuentas_modificacion ON ""AspNetUsers"" FOR UPDATE
  USING (""TenantId"" = {Tenant} OR ""Id"" = {Usuario})
  WITH CHECK ((""TenantId"" = {Tenant} OR ""Id"" = {Usuario})
          AND (""Id"" IS DISTINCT FROM {Usuario} OR ""TenantId"" = {TenantOrigen}));

CREATE POLICY cuentas_baja ON ""AspNetUsers"" FOR DELETE
  USING (""TenantId"" = {Tenant});
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS cuentas_baja ON ""AspNetUsers"";
DROP POLICY IF EXISTS cuentas_modificacion ON ""AspNetUsers"";
DROP POLICY IF EXISTS cuentas_alta ON ""AspNetUsers"";
DROP POLICY IF EXISTS cuentas_lectura ON ""AspNetUsers"";
ALTER TABLE ""AspNetUsers"" DISABLE ROW LEVEL SECURITY;
DROP FUNCTION IF EXISTS app_cuentas_por_email_normalizado(text);
DROP FUNCTION IF EXISTS app_cuenta_por_nombre_normalizado(text);
DROP FUNCTION IF EXISTS app_tenant_de_cuenta(uuid);
");
        }
    }
}
