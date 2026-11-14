using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// B-01 del plan backend Inbound: la línea Inbound «a presupuesto» del Producto Contratado (D-1, 2026-11-14) y el
    /// gate comercial DN-7, opción (a): la lectura entre Tenants solo responde al Tenant del Cliente empresarial, el lector, que
    /// tenga un Producto Contratado de esa línea vigente.
    /// <para>
    /// El CHECK <c>CK_ProductosContratados_Linea</c> admite el valor nuevo <c>Inbound</c>; lo genera EF a partir de
    /// <c>LineaProductoContratado</c>.
    /// </para>
    /// <para>
    /// El gate va en <c>app_inbound_tenant_contratista_del_contrato</c>, la auxiliar de la que dependen las tres
    /// funciones públicas: <c>app_inbound_autorizacion_aplicable</c> la usa para hallar la autorización (y de ella
    /// tiran <c>app_inbound_documentos_autorizados</c> y <c>app_inbound_archivo_autorizado</c>), y
    /// <c>app_inbound_autorizacion_del_contrato</c> la llama directamente. Sin la línea devuelve NULL, como un contrato
    /// sin vínculo, y las tres devuelven 0 filas sin dejar traza. Cuenta el Tenant beneficiario de la fila
    /// (<c>"TenantId" = p_lector</c>), nunca el Pagador TALVEG; una fila eliminada, finalizada o de otra línea no cuenta.
    /// La auxiliar corre con el propietario que evita la RLS (la llaman funciones SECURITY DEFINER), así que la
    /// comprobación ve el Producto Contratado del Tenant lector aunque su política de aislamiento lo oculte a otros.
    /// </para>
    /// <para>
    /// <c>CREATE OR REPLACE</c> con la misma firma, el mismo lenguaje, <c>STABLE</c>, el mismo <c>search_path</c> y sin
    /// <c>SECURITY DEFINER</c>: conserva propietario y privilegios (sin <c>EXECUTE</c> para ningún rol de la aplicación).
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo: amplía un CHECK (las filas existentes siguen valiendo) y
    /// reescribe una auxiliar que ningún rol de la aplicación llama; la ranura anterior sigue llamando a las mismas
    /// funciones públicas con la misma firma, y ningún código de la aplicación lee <c>ProductosContratados</c>, así que
    /// el valor nuevo nunca llega a un enum que no lo conozca. Lo que cambia para la ranura anterior es el resultado: sin
    /// la línea Inbound no hay lectura entre Tenants, que es la regla. Como ningún rol de la aplicación escribe aún
    /// <c>ProductosContratados</c> (lo hará el alta del Tenant del Cliente empresarial, B-04), desde esta migración ningún
    /// Tenant lee Inbound entre Tenants en ningún entorno hasta que tenga su línea.
    /// </para>
    /// <para>
    /// Sin vía de transición, a propósito (principio pre-cliente-real): a 2026-11-14 la lectura Inbound entre Tenants no
    /// es alcanzable desde la aplicación en ningún entorno, así que cerrarla no deja a nadie sin una lectura que tuviera.
    /// Ningún código de <c>CaeManager.Web</c> ni de <c>CaeManager.Infrastructure</c> envía <c>ObtenerAutorizacionInboundQuery</c>,
    /// <c>ObtenerDocumentosInboundQuery</c>, <c>ObtenerArchivoInboundQuery</c>, <c>RegistrarEvidenciaInboundCommand</c>,
    /// <c>ConcederAutorizacionLecturaCommand</c> ni <c>VincularTenantContratistaCommand</c>, y ninguna siembra crea
    /// contratos vinculados al Tenant de la Empresa contratista ni autorizaciones de lectura. Si eso cambia antes de que
    /// B-04 provisione las líneas, la provisión es una decisión de producto, no de esta migración.
    /// </para>
    /// <para>
    /// El Down no reabre la lectura: deja la auxiliar denegando siempre (el mismo cuerpo con <c>AND false</c> en lugar
    /// del gate), de modo que revertir no devuelve la lectura entre Tenants a un Tenant sin línea Inbound. Esa lectura
    /// solo se reabre con una migración nueva hacia delante, que es una decisión explícita. El cuerpo denegador no lee
    /// <c>ProductosContratados</c>, así que sigue siendo válido si un Down posterior borra la tabla
    /// (<c>AnadeProductosContratados</c>), y el de <c>AnadeLecturaInboundEntreTenants</c> la elimina con las demás.
    /// Se prefiere a un Down que falle porque mantiene la propiedad de seguridad sin bloquear la reversión del resto de la
    /// cadena. Después vuelve a estrechar el CHECK y falla si ya hay filas de la línea Inbound, en cualquier estado (el
    /// CHECK solo mira el valor de <c>Linea</c>): hay que borrarlas antes, con la identidad propietaria, decisión que no
    /// toma la migración.
    /// </para>
    /// </summary>
    public partial class AnadeLineaInboundProductoContratado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductosContratados_Linea",
                table: "ProductosContratados");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductosContratados_Linea",
                table: "ProductosContratados",
                sql: "\"Linea\" IN ('OutboundGestionPropia', 'Inbound')");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantContratistaId"
                    FROM public."ContratosAcceso" c
                   WHERE c."TenantId" = p_lector
                     AND c."Id" = p_contrato
                     AND NOT c."EstaEliminado"
                     AND c."FechaInicio" <= p_hoy
                     AND (c."FechaFin" IS NULL OR p_hoy <= c."FechaFin")
                     AND (c."FechaCierreReal" IS NULL OR p_hoy <= c."FechaCierreReal")
                     AND c."TenantContratistaId" IS NOT NULL
                     AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                  WHERE pc."TenantId" = p_lector AND pc."Linea" = 'Inbound'
                                    AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                    AND pc."VigenteDesdeUtc" <= pg_catalog.now());
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantContratistaId"
                    FROM public."ContratosAcceso" c
                   WHERE c."TenantId" = p_lector
                     AND c."Id" = p_contrato
                     AND NOT c."EstaEliminado"
                     AND c."FechaInicio" <= p_hoy
                     AND (c."FechaFin" IS NULL OR p_hoy <= c."FechaFin")
                     AND (c."FechaCierreReal" IS NULL OR p_hoy <= c."FechaCierreReal")
                     AND c."TenantContratistaId" IS NOT NULL
                     AND false;
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductosContratados_Linea",
                table: "ProductosContratados");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductosContratados_Linea",
                table: "ProductosContratados",
                sql: "\"Linea\" IN ('OutboundGestionPropia')");
        }
    }
}
