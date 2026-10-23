using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeLecturaInboundEntreTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccesosLecturaInbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutorizacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContratoAccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantLectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantOrigenLectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MomentoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccesosLecturaInbound", x => x.Id);
                    table.CheckConstraint("CK_AccesosLecturaInbound_DocumentoSegunTipo", "(\"Tipo\" = 'Listado' AND \"DocumentoId\" IS NULL) OR (\"Tipo\" = 'Apertura' AND \"DocumentoId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AccesosLecturaInbound_AutorizacionesLecturaDocumental_Tenan~",
                        columns: x => new { x.TenantId, x.AutorizacionId },
                        principalTable: "AutorizacionesLecturaDocumental",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccesosLecturaInbound_Autorizacion",
                table: "AccesosLecturaInbound",
                columns: new[] { "TenantId", "AutorizacionId", "MomentoUtc" });

            // Inbound M1, PR 3: la única lectura entre Tenants. X es el Tenant del Cliente empresarial (lector, dueño del
            // contrato de acceso C); Y, el Tenant de la Empresa contratista (Tenant propietario de los Documentos
            // originales y de la autorización A).
            //
            // "AccesosLecturaInbound" vive en Y con RLS estándar, como las tablas de AnadeAutorizacionesLecturaDocumental.
            // cae_app_runtime y cae_app_soporte solo tienen SELECT: la aplicación de Y lee quién leyó qué de lo suyo, y
            // nadie lo escribe ni lo reescribe salvo las funciones de abajo (propietario de la migración, no sujeto a RLS).
            //
            // Funciones públicas (plpgsql, SECURITY DEFINER, search_path fijado, nombres cualificados; EXECUTE solo para
            // cae_app_runtime). Exigen el contexto firmado (app_ctx_valido) con Tenant y usuario, y si no, 42501: «sin
            // contexto» es distinguible de «sin autorización» (vacío), para que un lector vacío en silencio no alimente
            // una baja por ausencia. El Tenant lector y el usuario salen de app_ctx_tenant_id() y app_ctx_usuario_id(),
            // nunca de un parámetro ni de la GUC app.tenant_id, que la propia conexión puede escribir. No son STABLE: las
            // dos que dan lectura insertan su fila en "AccesosLecturaInbound" en la misma llamada.
            //   app_inbound_autorizacion_del_contrato(C): como mucho una fila (la autorización sin revocar, o si no la
            //     última concedida) para que X sepa si puede validar. Solo si C cumple el paso 1. Sin traza: no lee Documentos.
            //   app_inbound_documentos_autorizados(C): metadatos de los Documentos del alcance, sin "ArchivoUrl". Traza Listado.
            //   app_inbound_archivo_autorizado(C, D): "ArchivoUrl" de un Documento del alcance. Traza Apertura.
            //
            // Condición común, en tres funciones auxiliares sin EXECUTE para nadie salvo el propietario (las llaman las de
            // arriba, que ya corren como él):
            //   1. app_inbound_tenant_contratista_del_contrato: C es de X, no eliminado, vigente hoy y vinculado (Y no
            //      NULL). Vigencia = ContratoAcceso.VigenteEn: FechaInicio <= hoy, FechaFin NULL o hoy <= FechaFin,
            //      FechaCierreReal NULL o hoy <= FechaCierreReal. Hoy es el día de negocio de Europe/Madrid (DiaDeNegocio).
            //   2. app_inbound_autorizacion_aplicable: A en Y para destinatario X y contrato C, "RevocadaEnUtc" IS NULL
            //      (nunca comparado con la hora: el runtime puede escribir cualquier valor no nulo al revocar) y vigente
            //      hoy (AutorizacionLecturaDocumental.VigenteEn).
            //   3. app_inbound_documentos_del_alcance: Documentos de Y no eliminados ni anonimizados, de tipo cuya
            //      Sensibilidad no es CategoriaEspecialSalud (el valor por defecto de todo tipo: falla cerrado), y que son
            //      de una Empresa propia de Y ("EsPropia", no eliminada) si A incluye los de Empresa, o de un Trabajador de
            //      los hijos de A que no esté eliminado ni anonimizado (la FK de la fila hija no lo ve).
            migrationBuilder.Sql(
                """
                ALTER TABLE public."AccesosLecturaInbound" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AccesosLecturaInbound" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AccesosLecturaInbound"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."AccesosLecturaInbound" FROM cae_app_runtime;
                GRANT SELECT ON TABLE public."AccesosLecturaInbound" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."AccesosLecturaInbound" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."AccesosLecturaInbound" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."AccesosLecturaInbound" FROM cae_app_aprovisionamiento;

                -- Id de la traza con la forma de un UUID v7 (48 bits de milisegundos, versión 7, variante RFC 9562).
                -- No usa uuidv7(), que no existe en el PostgreSQL 17 de algunas máquinas de desarrollo, ni un
                -- UUID v4 aleatorio, que prohíbe IdentificadoresDeEntidadUuidV7Tests para Ids de entidad.
                CREATE FUNCTION public.app_inbound_nuevo_id() RETURNS uuid
                    LANGUAGE plpgsql VOLATILE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_bytes bytea := decode(md5(random()::text || clock_timestamp()::text), 'hex');
                BEGIN
                  v_bytes := overlay(v_bytes PLACING substring(int8send((extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6);
                  v_bytes := set_byte(v_bytes, 6, (get_byte(v_bytes, 6) & 15) | 112);
                  v_bytes := set_byte(v_bytes, 8, (get_byte(v_bytes, 8) & 63) | 128);
                  RETURN encode(v_bytes, 'hex')::uuid;
                END
                $$;

                CREATE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
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
                     AND c."TenantContratistaId" IS NOT NULL;
                $$;

                CREATE FUNCTION public.app_inbound_autorizacion_aplicable(p_lector uuid, p_contrato uuid, p_hoy date)
                    RETURNS TABLE(autorizacion_id uuid, tenant_contratista uuid, incluye_empresa boolean)
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT a."Id", a."TenantId", a."IncluyeDocumentosEmpresa"
                    FROM public."AutorizacionesLecturaDocumental" a
                   WHERE a."TenantId" = public.app_inbound_tenant_contratista_del_contrato(p_lector, p_contrato, p_hoy)
                     AND a."TenantDestinatarioId" = p_lector
                     AND a."ContratoAccesoId" = p_contrato
                     AND a."RevocadaEnUtc" IS NULL
                     AND a."VigenciaDesde" <= p_hoy
                     AND (a."VigenciaHasta" IS NULL OR p_hoy <= a."VigenciaHasta");
                $$;

                CREATE FUNCTION public.app_inbound_documentos_del_alcance(p_tenant uuid, p_autorizacion uuid, p_incluye_empresa boolean)
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
                     AND td."Sensibilidad" <> 'CategoriaEspecialSalud'
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

                CREATE FUNCTION public.app_inbound_autorizacion_del_contrato(p_contrato uuid)
                    RETURNS TABLE("AutorizacionId" uuid, "VigenciaDesde" date, "VigenciaHasta" date, "Revocada" boolean,
                                  "IncluyeDocumentosEmpresa" boolean, "Trabajadores" integer)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_tenant_contratista uuid;
                BEGIN
                    IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                        RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                    END IF;
                    v_lector := public.app_ctx_tenant_id();
                    v_tenant_contratista := public.app_inbound_tenant_contratista_del_contrato(
                        v_lector, p_contrato, (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date);
                    IF v_tenant_contratista IS NULL THEN
                        RETURN;
                    END IF;
                    RETURN QUERY
                        SELECT a."Id", a."VigenciaDesde", a."VigenciaHasta", a."RevocadaEnUtc" IS NOT NULL, a."IncluyeDocumentosEmpresa",
                               (SELECT pg_catalog.count(*)::integer FROM public."AutorizacionesLecturaTrabajadores" h
                                 WHERE h."TenantId" = a."TenantId" AND h."AutorizacionId" = a."Id")
                          FROM public."AutorizacionesLecturaDocumental" a
                         WHERE a."TenantId" = v_tenant_contratista
                           AND a."TenantDestinatarioId" = v_lector
                           AND a."ContratoAccesoId" = p_contrato
                         ORDER BY (a."RevocadaEnUtc" IS NULL) DESC, a."ConcedidaEnUtc" DESC
                         LIMIT 1;
                END;
                $$;

                CREATE FUNCTION public.app_inbound_documentos_autorizados(p_contrato uuid)
                    RETURNS TABLE("DocumentoId" uuid, "TrabajadorId" uuid, "TrabajadorNombre" text, "TrabajadorApellidos" text,
                                  "TrabajadorDni" text, "TipoDocumentoNombre" text, "FechaEmision" date, "FechaVencimiento" date,
                                  "EstadoVigencia" integer)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_usuario uuid;
                    v_aut record;
                BEGIN
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
                               d."FechaEmision", d."FechaVencimiento", d."EstadoVigencia"
                          FROM public."Documentos" d
                          JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                          LEFT JOIN public."Trabajadores" t ON t."TenantId" = d."TenantId" AND t."Id" = d."TrabajadorId"
                         WHERE d."TenantId" = v_aut.tenant_contratista
                           AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                              v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa))
                         ORDER BY d."TrabajadorId" NULLS FIRST, td."Nombre", d."FechaEmision", d."Id";
                END;
                $$;

                CREATE FUNCTION public.app_inbound_archivo_autorizado(p_contrato uuid, p_documento uuid)
                    RETURNS TABLE("DocumentoId" uuid, "ArchivoUrl" text)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_usuario uuid;
                    v_aut record;
                    v_archivo text;
                BEGIN
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
                    SELECT d."ArchivoUrl" INTO v_archivo
                      FROM public."Documentos" d
                     WHERE d."TenantId" = v_aut.tenant_contratista
                       AND d."Id" = p_documento
                       AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                          v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa));
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    INSERT INTO public."AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                                                                "TenantOrigenLectorId", "UsuarioId", "DocumentoId", "Tipo", "MomentoUtc")
                        VALUES (public.app_inbound_nuevo_id(), v_aut.tenant_contratista, v_aut.autorizacion_id, p_contrato, v_lector,
                                NULLIF(public.app_ctx_tenant_origen_id(), v_lector), v_usuario, p_documento, 'Apertura', pg_catalog.now());
                    RETURN QUERY SELECT p_documento, v_archivo;
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_inbound_nuevo_id() FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE ALL ON FUNCTION public.app_inbound_tenant_contratista_del_contrato(uuid, uuid, date) FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE ALL ON FUNCTION public.app_inbound_autorizacion_aplicable(uuid, uuid, date) FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE ALL ON FUNCTION public.app_inbound_documentos_del_alcance(uuid, uuid, boolean) FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE ALL ON FUNCTION public.app_inbound_autorizacion_del_contrato(uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE ALL ON FUNCTION public.app_inbound_documentos_autorizados(uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE ALL ON FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_inbound_autorizacion_del_contrato(uuid) TO cae_app_runtime;
                GRANT EXECUTE ON FUNCTION public.app_inbound_documentos_autorizados(uuid) TO cae_app_runtime;
                GRANT EXECUTE ON FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid);
                DROP FUNCTION public.app_inbound_documentos_autorizados(uuid);
                DROP FUNCTION public.app_inbound_autorizacion_del_contrato(uuid);
                DROP FUNCTION public.app_inbound_documentos_del_alcance(uuid, uuid, boolean);
                DROP FUNCTION public.app_inbound_autorizacion_aplicable(uuid, uuid, date);
                DROP FUNCTION public.app_inbound_tenant_contratista_del_contrato(uuid, uuid, date);
                DROP FUNCTION public.app_inbound_nuevo_id();
                """);

            migrationBuilder.DropTable(
                name: "AccesosLecturaInbound");
        }
    }
}
