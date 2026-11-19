using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// D-12 (DECISION_LOG 2026-11-16 y 2026-11-17): nadie valida en Inbound la versión de un Documento que subió él mismo.
/// <c>app_inbound_documentos_autorizados</c> devuelve una columna más, <c>"SubidoPorQuienLee"</c>: verdadera si el usuario
/// del contexto firmado (<c>app_ctx_usuario_id()</c>, el mismo que ya firma la traza de la lectura) coincide con
/// cualquiera de los dos autores de la versión, <c>"SubidoPorUsuarioId"</c> o <c>"SubidoPorActorRealUsuarioId"</c>
/// (D-12.2). Un autor nulo, el de las filas anteriores a la columna, nunca coincide (D-12.1, falso por el
/// <c>COALESCE</c>); el autor no se lee de la auditoría.
/// <para>
/// <b>Por qué un booleano y no los identificadores.</b> Es lo que menos expone del Tenant TEC al Tenant TCE: el Tenant
/// lector no recibe el identificador de ningún usuario del Tenant propietario, solo si esa versión la subió quien lee. Y
/// la función sigue sin parámetros que elijan usuario: la identidad sale del contexto firmado, como el Tenant lector, así
/// que no sirve de oráculo para preguntar por otra persona.
/// </para>
/// <para>
/// <b>Lo demás no cambia</b>: misma firma, mismo <c>SECURITY DEFINER</c>, mismo <c>search_path</c>, la guardia de
/// <c>FalloCerradoLecturaInbound</c> justo después del <c>BEGIN</c>, la misma traza, el mismo alcance
/// (<c>app_inbound_documentos_del_alcance</c>: eliminados, anonimizados, sustituidos y salud) y la misma comprobación de
/// revocación. Como cambia el tipo de retorno, hace falta <c>DROP</c> y <c>CREATE</c>, y los privilegios se vuelven a dar
/// igual que antes. La ranura anterior llama con la misma firma y su lector ignora la columna nueva. El Down restaura el
/// cuerpo vigente exacto.
/// </para>
/// </summary>
public partial class MarcaInboundSubidoPorQuienLee : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Definicion(conMarca: true));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Definicion(conMarca: false));

    // Fin de línea fijo: la guardia de FalloCerradoLecturaInbound se busca literal, con LF.
    private static string Definicion(bool conMarca)
    {
        var columna = conMarca ? ", \"SubidoPorQuienLee\" boolean" : string.Empty;
        var expresion = conMarca
            ? ",\n               COALESCE(d.\"SubidoPorUsuarioId\" = v_usuario OR d.\"SubidoPorActorRealUsuarioId\" = v_usuario, false)"
            : string.Empty;
        return $$"""
            DROP FUNCTION public.app_inbound_documentos_autorizados(uuid);
            CREATE FUNCTION public.app_inbound_documentos_autorizados(p_contrato uuid)
                RETURNS TABLE("DocumentoId" uuid, "TrabajadorId" uuid, "TrabajadorNombre" text, "TrabajadorApellidos" text,
                              "TrabajadorDni" text, "TipoDocumentoNombre" text, "FechaEmision" date, "FechaVencimiento" date,
                              "EstadoVigencia" integer, "SoloConclusionAptitud" boolean{{columna}})
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            #variable_conflict use_column
            DECLARE
                v_lector uuid;
                v_usuario uuid;
                v_aut record;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la lectura Inbound no puede comprobar los datos del Tenant propietario', current_user;
                END IF;
                IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                    RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                END IF;
                v_lector := public.app_ctx_tenant_id();
                v_usuario := public.app_ctx_usuario_id();
                SELECT * INTO v_aut FROM public.app_inbound_autorizacion_aplicable(
                    v_lector, p_contrato, (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date);
                IF NOT FOUND THEN
                    RETURN;
                END IF;
                INSERT INTO public."AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                                                            "TenantOrigenLectorId", "UsuarioId", "DocumentoId", "Tipo", "MomentoUtc")
                    VALUES (public.app_inbound_nuevo_id(), v_aut.tenant_contratista, v_aut.autorizacion_id, p_contrato, v_lector,
                            NULLIF(public.app_ctx_tenant_origen_id(), v_lector), v_usuario, NULL, 'Listado', pg_catalog.now());
                RETURN QUERY
                    SELECT d."Id", d."TrabajadorId", t."Nombre"::text, t."Apellidos"::text, t."Dni"::text, td."Nombre"::text,
                           d."FechaEmision", d."FechaVencimiento", d."EstadoVigencia", td."SoloConclusionAptitud"{{expresion}}
                      FROM public."Documentos" d
                      JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                      LEFT JOIN public."Trabajadores" t ON t."TenantId" = d."TenantId" AND t."Id" = d."TrabajadorId"
                     WHERE d."TenantId" = v_aut.tenant_contratista
                       AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                          v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa))
                       AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaDocumental" a
                                    WHERE a."TenantId" = v_aut.tenant_contratista AND a."Id" = v_aut.autorizacion_id
                                      AND a."RevocadaEnUtc" IS NULL)
                     ORDER BY d."TrabajadorId" NULLS FIRST, td."Nombre", d."FechaEmision", d."Id";
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_inbound_documentos_autorizados(uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_inbound_documentos_autorizados(uuid) TO cae_app_runtime;
            """.ReplaceLineEndings("\n");
    }
}
