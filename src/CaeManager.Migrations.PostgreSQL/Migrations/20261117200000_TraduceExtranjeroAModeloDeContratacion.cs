using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// El extranjero como modelo de contratación (decisión de Chris de 2026-11-17, DECISION_LOG): <c>TipoContratacion.Extranjero</c>
    /// = 3 y su bit <c>TiposContratacion.Extranjero</c> = 8, con lo que <c>TiposContratacion.Todos</c> pasa de 7 a 15. La máscara
    /// es desde ahora el único criterio para pedir algo a un extranjero, y <c>RequisitosPlantilla."SePideA"</c> deja de leerse
    /// (la columna se retira en otro incremento).
    /// <para>
    /// Qué traduce. (1) <c>TiposDocumento."TiposContratacionDeSerie"</c>: donde era exactamente el Todos anterior (7) pasa a 15,
    /// para que el tipo se siga pidiendo de serie a todos, ahora también al Extranjero; cualquier otra máscara se queda como está.
    /// (2) <c>RequisitosPlantilla."TiposContratacion"</c> de un requisito de Trabajador: con <c>"SePideA"</c> = Extranjeros, solo el
    /// bit Extranjero (8); con Nacionales, la misma máscara sin el bit Extranjero; con Todos, 7 pasa a 15. (3) Un requisito de
    /// Empresa o de vehículo o máquina tiene siempre la máscara Todos (el dominio lo exige), así que su 7 pasa a 15 sea cual sea
    /// su <c>"SePideA"</c>, que se queda intacto. <c>"SePideA"</c> no se reescribe en ninguna fila.
    /// </para>
    /// <para>
    /// Qué no toca. Ningún Trabajador cambia de modelo de contratación: no se deduce el Extranjero del documento de identidad ni
    /// de la nacionalidad. Facturación tampoco: el extranjero cobrable sigue saliendo del documento o de la marca a mano
    /// (<c>CriterioExtranjeroCobrable</c>, <c>VisitaTrabajador."EsExtranjeroCobrable"</c>). El CHECK
    /// <c>CK_RequisitosPlantilla_TiposContratacion</c> se rehace para admitir el bit 8 (se quita antes de traducir y se pone
    /// después).
    /// </para>
    /// <para>
    /// La lectura inversa X-2 (<c>app_inbound_tec_requerimientos()</c>, P0a) devolvía <c>"SePideA"</c>: se sustituye por la misma
    /// función sin esa columna, con el mismo cuerpo, el mismo <c>SECURITY DEFINER</c>, el mismo propietario (el de las
    /// migraciones) y los mismos <c>REVOKE</c> y <c>GRANT</c>. Cambia la firma, así que es <c>DROP</c> y <c>CREATE</c>; ninguna
    /// ranura la llama todavía desde el código.
    /// </para>
    /// <para>
    /// Down revierte con pérdida: un requisito que quedó en 8 vuelve a 7 (el «Se pide a» intacto le devuelve el sentido de
    /// Extranjeros), los 15 vuelven a 7 y la función recupera su columna. Lo escrito después de la migración no se distingue de lo
    /// traducido: un requisito o un tipo de documento que alguien acotó al Extranjero vuelve a pedirse a todos, y un Trabajador
    /// dado de alta como Extranjero conserva el 3, que la ranura anterior no conoce.
    /// </para>
    /// <para>
    /// NO es compatible con la ranura anterior mientras drena: su <c>TiposContratacion.Todos</c> vale 7 y normaliza con
    /// <c>&amp; 7</c>. Guardar allí un tipo de documento de Trabajador que ya esté en 15 lo deja en 7 (deja de pedirse al
    /// Extranjero sin aviso), editar un requisito con máscara 8 o 15 falla con «tipo de contratación conocido» y un requisito
    /// de Empresa creado allí nace en 7. La ventana es la del relevo azul/verde; se acepta y se declara en la PR.
    /// </para>
    /// </summary>
    public partial class TraduceExtranjeroAModeloDeContratacion : Migration
    {
        // Mismo guardia que ProtegeLecturaIaInicialSalud: sin un migrador que evite la RLS, los UPDATE no verían ninguna fila
        // y la traducción quedaría vacía en silencio.
        private const string GuardiaMigrador =
            """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'TraduceExtranjeroAModeloDeContratacion necesita un migrador superusuario o con BYPASSRLS';
                END IF;
            END
            $$;
            """;

        public const string TraduceMascaras =
            """
            UPDATE public."TiposDocumento"
               SET "TiposContratacionDeSerie" = 15
             WHERE "TiposContratacionDeSerie" = 7;

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = 8
             WHERE "Entidad" = 'Trabajador' AND "SePideA" = 'Extranjeros';

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = "TiposContratacion" & ~8
             WHERE "Entidad" = 'Trabajador' AND "SePideA" = 'Nacionales';

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = 15
             WHERE "TiposContratacion" = 7
               AND ("Entidad" <> 'Trabajador' OR "SePideA" = 'Todos');
            """;

        public const string RevierteMascaras =
            """
            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = 7
             WHERE "TiposContratacion" IN (8, 15);

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = "TiposContratacion" & 7
             WHERE ("TiposContratacion" & ~7) <> 0;

            UPDATE public."TiposDocumento"
               SET "TiposContratacionDeSerie" = 7
             WHERE "TiposContratacionDeSerie" IN (8, 15);

            UPDATE public."TiposDocumento"
               SET "TiposContratacionDeSerie" = "TiposContratacionDeSerie" & 7
             WHERE ("TiposContratacionDeSerie" & ~7) <> 0;
            """;

        public const string CreaFuncionRequerimientosTecSinSePideA =
            """
            DROP FUNCTION public.app_inbound_tec_requerimientos();
            CREATE FUNCTION public.app_inbound_tec_requerimientos()
                RETURNS TABLE("ContratoAccesoId" uuid, "TenantTceNombre" text, "CentroId" uuid, "CentroNombre" text,
                              "ClienteEmpresarialNombre" text, "RequisitoId" uuid, "TipoDocumentoId" uuid, "TipoDocumentoNombre" text,
                              "Entidad" text, "Criterio" text, "ModoVigencia" text, "VigenciaMeses" integer, "ToleranciaDias" integer,
                              "BloqueaAcceso" boolean, "AmbitoDocumento" text, "CapaPlantilla" text, "TiposContratacion" integer)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            #variable_conflict use_column
            DECLARE
                v_tec uuid;
                v_hoy date;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la lectura Inbound no puede comprobar los datos del Tenant propietario', current_user;
                END IF;
                IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                    RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                END IF;
                v_tec := public.app_ctx_tenant_id();
                v_hoy := (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date;
                RETURN QUERY
                    SELECT c."Id", tn."Nombre"::text, ce."Id", ce."Nombre"::text, ec."RazonSocial"::text,
                           r."Id", r."TipoDocumentoId", td."Nombre"::text, r."Entidad"::text, r."Criterio"::text,
                           r."ModoVigencia"::text, r."VigenciaMeses", r."ToleranciaDias", r."BloqueaAcceso",
                           r."AmbitoDocumento"::text, p."Tipo"::text, r."TiposContratacion"
                      FROM public."ContratosAcceso" c
                      JOIN public."Tenants" tn ON tn."Id" = c."TenantId"
                      JOIN public."Centros" ce ON ce."TenantId" = c."TenantId" AND ce."Id" = c."CentroId"
                      JOIN public."Empresas" ec ON ec."TenantId" = c."TenantId" AND ec."Id" = c."ClienteEmpresarialId"
                      JOIN public."PlantillasRequisitos" p
                        ON p."TenantId" = c."TenantId"
                       AND p."Activa" AND NOT p."EstaEliminado"
                       AND ((p."Tipo" = 'DeCentro' AND p."CentroId" = c."CentroId")
                            OR (p."Tipo" = 'ExigenciasClienteEmpresarial' AND p."ClienteEmpresarialId" = c."ClienteEmpresarialId"))
                      JOIN public."RequisitosPlantilla" r
                        ON r."TenantId" = p."TenantId" AND r."PlantillaId" = p."Id" AND r."Activo"
                      JOIN public."TiposDocumento" td ON td."TenantId" = r."TenantId" AND td."Id" = r."TipoDocumentoId"
                     WHERE c."TenantContratistaId" = v_tec
                       AND NOT c."EstaEliminado"
                       AND c."Estado" = 'Activo'
                       AND c."FechaCierreReal" IS NULL
                       AND c."FechaInicio" <= v_hoy
                       AND (c."FechaFin" IS NULL OR v_hoy <= c."FechaFin")
                       AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                    WHERE pc."TenantId" = c."TenantId" AND pc."Linea" = 'Inbound'
                                      AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                      AND pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp())
                     ORDER BY c."Id", p."Tipo", td."Nombre", r."Id";
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_inbound_tec_requerimientos() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_inbound_tec_requerimientos() TO cae_app_runtime;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardiaMigrador);

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla");

            migrationBuilder.Sql(TraduceMascaras);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla",
                sql: "\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~15) = 0");

            migrationBuilder.Sql(CreaFuncionRequerimientosTecSinSePideA);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardiaMigrador);

            migrationBuilder.Sql(
                "DROP FUNCTION public.app_inbound_tec_requerimientos();\n" + AnadeLecturaRequerimientosTec.CreaFuncionRequerimientosTec);

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla");

            migrationBuilder.Sql(RevierteMascaras);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla",
                sql: "\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~7) = 0");
        }
    }
}
