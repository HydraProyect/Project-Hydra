using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// B-02b de Inbound: <c>app_operacion_inbound_amparada(uuid)</c>, el amparo de una Asignación de Operación Inbound
    /// externa (decisión de Chris del 2026-11-16, opción A): sin DelegacionTenant, basta con que el Tenant propietario
    /// (el Tenant TCE) tenga la línea Inbound de su Producto Contratado vigente. Se revoca cerrando la operación.
    /// <para>
    /// La lee <c>TenantsBeneficiariosAutorizados.OperacionesInboundQueAutorizan</c> (vía <c>FuncionAmparoInbound</c>) al
    /// elegir y al revalidar el papel Inbound. Va en PostgreSQL porque la persona del Operador CAE externo no ve, ni debe
    /// ver, la fila de <c>ProductosContratados</c> del Tenant TCE (RLS <c>aislamiento_tenant</c>; la fila lleva el
    /// Pagador TALVEG y el tramo).
    /// </para>
    /// <para>
    /// <c>SECURITY DEFINER</c> con el propietario de las tablas y <c>search_path</c> fijo, como
    /// <c>app_operacion_amparada_por_su_origen</c> (#2237), y con su mismo criterio: comprueba que su propietario evita la
    /// RLS en vez de responder <c>false</c> a todo; recibe solo el Id de la operación y responde solo un booleano; solo
    /// responde sobre una operación Inbound de la que el Tenant activo o el de origen del contexto firmado es uno de los
    /// dos extremos, salvo para una sesión cuyo <c>session_user</c> ya evita la RLS. Una operación de otro servicio, una
    /// interna o una que no existe no está amparada. La vigencia de la línea es la del gate DN-7
    /// (<c>GateInboundConRelojDeEvaluacion</c>): <c>Vigente</c>, no eliminada y con el inicio ya alcanzado según
    /// <c>clock_timestamp()</c>, por eso es <c>VOLATILE</c>. No mira el estado ni la vigencia de la propia operación: los
    /// pone la consulta que la llama.
    /// </para>
    /// <para>
    /// <c>EXECUTE</c> para <c>cae_app_runtime</c>, la identidad que elige y revalida el contexto, y <c>cae_app_soporte</c>,
    /// para que una revalidación bajo Sesión Privilegiada responda <c>false</c> y no un error de permisos. No concede a ningún
    /// rol lectura ni escritura nueva, no toca ninguna política y es compatible con la ranura anterior, que no la llama:
    /// la ranura anterior sigue sin autorizar ninguna Asignación de Operación Inbound (B-02a).
    /// </para>
    /// </summary>
    public partial class AnadeAmparoDeOperacionInbound : Migration
    {
        public const string CreaFuncionAmparoInbound =
            """
            CREATE FUNCTION public.app_operacion_inbound_amparada(p_operacion uuid)
                RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_tenant uuid := public.app_ctx_tenant_id();
                v_tenant_origen uuid := public.app_ctx_tenant_origen_id();
                v_propietario uuid;
                v_operador uuid;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación del amparo Inbound no vería ninguna línea', current_user;
                END IF;
                SELECT o."PropietarioTenantId", o."OperadorTenantId"
                  INTO v_propietario, v_operador
                  FROM public."AsignacionesOperacion" o
                 WHERE o."Id" = p_operacion
                   AND o."Servicio" = 'Inbound';
                -- Solo Inbound: el amparo de Outbound es la DelegacionTenant de origen (app_operacion_amparada_por_su_origen).
                IF NOT FOUND THEN
                    RETURN false;
                END IF;
                IF NOT (EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = session_user AND (rolsuper OR rolbypassrls))
                        OR COALESCE(v_tenant IN (v_propietario, v_operador), false)
                        OR COALESCE(v_tenant_origen IN (v_propietario, v_operador), false)) THEN
                    RETURN false;
                END IF;
                -- Solo externa: una operación interna no abre ningún contexto delegado.
                IF v_propietario = v_operador THEN
                    RETURN false;
                END IF;
                RETURN EXISTS (
                    SELECT 1 FROM public."ProductosContratados" pc
                     WHERE pc."TenantId" = v_propietario
                       AND pc."Linea" = 'Inbound'
                       AND pc."Estado" = 'Vigente'
                       AND NOT pc."EstaEliminado"
                       AND pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp());
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_operacion_inbound_amparada(uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.app_operacion_inbound_amparada(uuid) TO cae_app_runtime, cae_app_soporte;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreaFuncionAmparoInbound);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.app_operacion_inbound_amparada(uuid);");
        }
    }
}
