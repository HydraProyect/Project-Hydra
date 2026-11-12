using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// EP-6, opción (d), 2026-11-12: cierre provisional de la lectura entre Tenants de los Documentos de Empresa.
/// <c>app_inbound_documentos_del_alcance</c> devolvía, si la autorización incluye los de Empresa, los Documentos de
/// cualquier Empresa propia no eliminada del Tenant de la Empresa contratista (el Tenant propietario de los datos). Con
/// la ampliación de grupo de D-P11 ese Tenant puede tener varias Empresas propias legítimas, y el Tenant del Cliente
/// empresarial leería los Documentos de una sociedad hermana que no es parte del ContratoAcceso: el contrato no guarda
/// qué Empresa del Tenant contratista lo firma y la autorización solo lleva el booleano de los de Empresa.
/// <para>
/// Regla nueva: los Documentos de Empresa salen solo si el Tenant contratista tiene exactamente una Empresa propia no
/// eliminada (una propia eliminada no cuenta). Con dos o más, ninguno: falla cerrado. Los Documentos de Trabajador
/// autorizados y el resto de filtros (eliminados, anonimizados, sustituidos, tipos de categoría especial de salud salvo
/// los de solo conclusión de aptitud, Trabajadores de la autorización no eliminados ni anonimizados) no cambian: son el
/// texto vigente de <c>AnadeEvidenciasInbound</c>, sin tocar. La relación definitiva (opción (a): la Empresa propia que
/// expone la autorización, elegida por el Tenant propietario) queda para el MVP Inbound. Esta nota sustituye, para la
/// rama de Empresa, a la condición 3 de la cabecera de <c>AnadeLecturaInboundEntreTenants</c>, que no se edita.
/// </para>
/// <para>
/// <c>CREATE OR REPLACE</c> con la misma firma, el mismo lenguaje, <c>STABLE</c>, el mismo <c>search_path</c> y sin
/// <c>SECURITY DEFINER</c>, como la versión vigente: conserva propietario y privilegios (sin <c>EXECUTE</c> para ningún
/// rol de la aplicación) y no cambia a sus dos llamantes, <c>app_inbound_documentos_autorizados</c> y
/// <c>app_inbound_archivo_autorizado</c>, que la consultan con el propietario que evita la RLS, así que el recuento ve
/// todas las Empresas del Tenant contratista. El recuento no depende del Documento: se evalúa una vez por consulta. El
/// Down restaura el cuerpo vigente exacto. La ranura anterior sigue llamando a la misma función con la misma firma.
/// </para>
/// </summary>
public partial class LecturaInboundEmpresaPropiaUnica : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.app_inbound_documentos_del_alcance(p_tenant uuid, p_autorizacion uuid, p_incluye_empresa boolean)
                RETURNS SETOF uuid
                LANGUAGE sql STABLE
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
              SELECT d."Id"
                FROM public."Documentos" d
                JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
               WHERE d."TenantId" = p_tenant
                 AND NOT d."EstaEliminado"
                 AND d."AnonimizadoEnUtc" IS NULL
                 AND d."SustituidoEnUtc" IS NULL
                 AND (td."Sensibilidad" <> 'CategoriaEspecialSalud' OR td."SoloConclusionAptitud")
                 AND ((p_incluye_empresa
                       AND d."EmpresaId" IS NOT NULL
                       AND EXISTS (SELECT 1 FROM public."Empresas" e
                                    WHERE e."TenantId" = p_tenant AND e."Id" = d."EmpresaId"
                                      AND e."EsPropia" AND NOT e."EstaEliminado")
                       AND (SELECT count(*) FROM public."Empresas" e2
                             WHERE e2."TenantId" = p_tenant AND e2."EsPropia" AND NOT e2."EstaEliminado") = 1)
                      OR (d."TrabajadorId" IS NOT NULL
                          AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaTrabajadores" h
                                        JOIN public."Trabajadores" t ON t."TenantId" = h."TenantId" AND t."Id" = h."TrabajadorId"
                                       WHERE h."TenantId" = p_tenant AND h."AutorizacionId" = p_autorizacion
                                         AND h."TrabajadorId" = d."TrabajadorId"
                                         AND NOT t."EstaEliminado" AND t."AnonimizadoEnUtc" IS NULL)));
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.app_inbound_documentos_del_alcance(p_tenant uuid, p_autorizacion uuid, p_incluye_empresa boolean)
                RETURNS SETOF uuid
                LANGUAGE sql STABLE
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
              SELECT d."Id"
                FROM public."Documentos" d
                JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
               WHERE d."TenantId" = p_tenant
                 AND NOT d."EstaEliminado"
                 AND d."AnonimizadoEnUtc" IS NULL
                 AND d."SustituidoEnUtc" IS NULL
                 AND (td."Sensibilidad" <> 'CategoriaEspecialSalud' OR td."SoloConclusionAptitud")
                 AND ((p_incluye_empresa
                       AND d."EmpresaId" IS NOT NULL
                       AND EXISTS (SELECT 1 FROM public."Empresas" e
                                    WHERE e."TenantId" = p_tenant AND e."Id" = d."EmpresaId"
                                      AND e."EsPropia" AND NOT e."EstaEliminado"))
                      OR (d."TrabajadorId" IS NOT NULL
                          AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaTrabajadores" h
                                        JOIN public."Trabajadores" t ON t."TenantId" = h."TenantId" AND t."Id" = h."TrabajadorId"
                                       WHERE h."TenantId" = p_tenant AND h."AutorizacionId" = p_autorizacion
                                         AND h."TrabajadorId" = d."TrabajadorId"
                                         AND NOT t."EstaEliminado" AND t."AnonimizadoEnUtc" IS NULL)));
            $$;
            """);
    }
}
