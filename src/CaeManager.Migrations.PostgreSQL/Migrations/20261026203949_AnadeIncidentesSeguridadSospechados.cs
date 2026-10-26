using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeIncidentesSeguridadSospechados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncidentesSeguridadSospechados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Senal = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SujetoClave = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DetectadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UltimaDeteccionEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Detecciones = table.Column<int>(type: "integer", nullable: false),
                    TenantsAfectados = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Evidencia = table.Column<string>(type: "jsonb", nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConfirmadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentesSeguridadSospechados", x => x.Id);
                    table.CheckConstraint("CK_IncidentesSeguridadSospechados_ConfirmadoConHora", "(\"Estado\" <> 'Confirmado') OR (\"ConfirmadoEnUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_IncidentesSeguridadSospechados_Detecciones", "\"Detecciones\" >= 1");
                    table.CheckConstraint("CK_IncidentesSeguridadSospechados_Estado", "\"Estado\" IN ('Sospechado', 'EnEvaluacion', 'Confirmado', 'Descartado')");
                    table.CheckConstraint("CK_IncidentesSeguridadSospechados_Senal", "\"Senal\" IN ('InicioSesionNoReconocido', 'ExtraccionMasiva', 'AccesoRepetidoAOtroTenant')");
                });

            migrationBuilder.CreateTable(
                name: "TransicionesIncidenteSeguridad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidenteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstadoAnterior = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    EstadoNuevo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OcurridoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransicionesIncidenteSeguridad", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransicionesIncidenteSeguridad_IncidentesSeguridadSospechad~",
                        column: x => x.IncidenteId,
                        principalTable: "IncidentesSeguridadSospechados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentesSeguridadSospechados_Estado_DetectadoEnUtc",
                table: "IncidentesSeguridadSospechados",
                columns: new[] { "Estado", "DetectadoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentesSeguridadSospechados_Senal_SujetoClave_DetectadoE~",
                table: "IncidentesSeguridadSospechados",
                columns: new[] { "Senal", "SujetoClave", "DetectadoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TransicionesIncidenteSeguridad_IncidenteId_OcurridoEnUtc",
                table: "TransicionesIncidenteSeguridad",
                columns: new[] { "IncidenteId", "OcurridoEnUtc" });

            // Incidentes de seguridad sospechados: plano de Plataforma, sin TenantId (un incidente puede
            // afectar a varios Tenants, que se guardan como dato en "TenantsAfectados").
            //
            // RLS + FORCE en las dos tablas y una sola política, de lectura: actor_de_plataforma_lee, para
            // quien tenga una concesión AdminPlataforma global vigente (app_es_admin_plataforma_global sobre
            // app_ctx_usuario_id(), la identidad del contexto RLS firmado). Ningún rol de la aplicación
            // escribe directamente: cae_app_runtime solo tiene SELECT, y soporte y aprovisionamiento nada.
            //
            // La única puerta de escritura es app_registrar_incidente_seguridad (SECURITY DEFINER): la señal
            // la dispara cualquier usuario de cualquier Tenant, que no puede leer los incidentes y por tanto
            // tampoco deduplicar consultándolos. La función toma un cerrojo transaccional por (señal, sujeto),
            // suma una detección al incidente no descartado de la misma señal y sujeto nacido dentro de la
            // ventana (acotada a [60 s, 24 h]) o crea uno nuevo en Sospechado con su primera transición, y
            // devuelve solo el identificador.
            //
            // EXECUTE para los tres roles de la aplicación. El adaptador (RegistroIncidentesSeguridadEnBase)
            // abre su propio ámbito de DI y su propia conexión, y TenantRlsConnectionInterceptor le pone el
            // rol al abrirla: dentro de una Sesión Privilegiada, cae_app_soporte; y si la señal nace dentro
            // de un ámbito de elevación de Aprovisionamiento (PD-A3), que es un AsyncLocal y fluye al ámbito
            // nuevo, cae_app_aprovisionamiento. Una señal nacida ahí (p. ej. una exportación masiva del
            // Actor real) tiene que quedar registrada. EXECUTE no da a ninguno de los dos más que esto:
            // ninguno lee ni escribe las tablas.
            //
            // Límites de entrada, porque la llaman roles que no leen la tabla: como mucho 50 Tenants
            // (IncidenteSeguridadSospechado.MaximoTenantsAfectados; al agrupar, la unión se corta en 50 y
            // la evidencia lo dice), evidencia de 8 KB como mucho, señal y sujeto con su longitud de
            // columna. El sujeto se contrasta con app_ctx_usuario_id(), la identidad del contexto RLS
            // firmado, cuando existe: si no coincide, el registro no se rechaza, se marca en la evidencia
            // (sujeto_distinto_del_contexto y contexto_usuario_id). Rechazar perdería señales legítimas:
            // en una Sesión Privilegiada el sujeto es el Actor real y el contexto, el Usuario simulado; y
            // la persona titular puede confirmar «No he sido yo» desde un navegador con otra sesión
            // abierta. Un incidente nace Sospechado y exige confirmación humana: la marca basta para que
            // un registro forzado se vea como tal. Y la deduplicación nunca mezcla un registro marcado con
            // uno sin marcar: si lo hiciera, un incidente plantado sobre el sujeto de otra persona
            // absorbería su señal real dentro de la ventana y suprimiría su alerta.
            //
            // Como app_purgar_inicios_sesion, depende de que su propietario (el rol de las migraciones) sea
            // superusuario o tenga BYPASSRLS: con FORCE, sin eso el INSERT chocaría con RLS. Lo comprueba y
            // falla en vez de callar.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."IncidentesSeguridadSospechados" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."IncidentesSeguridadSospechados" FORCE ROW LEVEL SECURITY;
                CREATE POLICY actor_de_plataforma_lee ON public."IncidentesSeguridadSospechados"
                    FOR SELECT TO cae_app_runtime
                    USING ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))));
                REVOKE ALL ON TABLE public."IncidentesSeguridadSospechados" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."IncidentesSeguridadSospechados" TO cae_app_runtime;

                ALTER TABLE public."TransicionesIncidenteSeguridad" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."TransicionesIncidenteSeguridad" FORCE ROW LEVEL SECURITY;
                CREATE POLICY actor_de_plataforma_lee ON public."TransicionesIncidenteSeguridad"
                    FOR SELECT TO cae_app_runtime
                    USING ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))));
                REVOKE ALL ON TABLE public."TransicionesIncidenteSeguridad" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."TransicionesIncidenteSeguridad" TO cae_app_runtime;

                CREATE FUNCTION public.app_registrar_incidente_seguridad(
                    p_incidente_id uuid, p_transicion_id uuid, p_senal text, p_sujeto text,
                    p_tenants uuid[], p_evidencia jsonb, p_ventana_segundos integer)
                    RETURNS TABLE(incidente_id uuid, es_nuevo boolean)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_existente uuid;
                    v_ventana integer;
                    v_tenants uuid[];
                    v_ctx_usuario uuid;
                    v_distinto boolean;
                    v_evidencia jsonb;
                    v_union uuid[];
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: el registro de incidentes de seguridad no podría escribir', current_user;
                    END IF;
                    IF p_incidente_id IS NULL OR p_transicion_id IS NULL OR p_senal IS NULL OR p_sujeto IS NULL
                       OR p_evidencia IS NULL OR pg_catalog.jsonb_typeof(p_evidencia) <> 'object' THEN
                        RAISE EXCEPTION 'Registro de incidente de seguridad incompleto' USING ERRCODE = '22004';
                    END IF;
                    IF pg_catalog.length(p_senal) > 40 OR pg_catalog.length(p_sujeto) > 80 THEN
                        RAISE EXCEPTION 'Señal o sujeto del incidente de seguridad demasiado largos' USING ERRCODE = '22001';
                    END IF;
                    IF pg_catalog.cardinality(COALESCE(p_tenants, '{}'::uuid[])) > 50 THEN
                        RAISE EXCEPTION 'Más de 50 Tenants afectados en un registro de incidente de seguridad' USING ERRCODE = '22023';
                    END IF;
                    IF pg_catalog.octet_length(p_evidencia::text) > 8192 THEN
                        RAISE EXCEPTION 'Evidencia del incidente de seguridad de más de 8 KB' USING ERRCODE = '22001';
                    END IF;

                    v_ctx_usuario := public.app_ctx_usuario_id();
                    v_distinto := v_ctx_usuario IS NOT NULL AND p_sujeto <> 'usuario:' || v_ctx_usuario::text;
                    v_evidencia := p_evidencia;
                    IF v_distinto THEN
                        v_evidencia := v_evidencia || pg_catalog.jsonb_build_object(
                            'sujeto_distinto_del_contexto', true, 'contexto_usuario_id', v_ctx_usuario::text);
                    END IF;

                    v_ventana := LEAST(GREATEST(COALESCE(p_ventana_segundos, 3600), 60), 86400);
                    v_tenants := ARRAY(SELECT DISTINCT t FROM pg_catalog.unnest(COALESCE(p_tenants, '{}'::uuid[])) AS t
                                        WHERE t IS NOT NULL ORDER BY t);

                    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_senal || '|' || p_sujeto, 0));

                    SELECT i."Id" INTO v_existente
                      FROM public."IncidentesSeguridadSospechados" i
                     WHERE i."Senal" = p_senal AND i."SujetoClave" = p_sujeto AND i."Estado" <> 'Descartado'
                       AND i."DetectadoEnUtc" >= pg_catalog.now() - pg_catalog.make_interval(secs => v_ventana)
                       AND (i."Evidencia" ? 'sujeto_distinto_del_contexto') = v_distinto
                     ORDER BY i."DetectadoEnUtc" DESC
                     LIMIT 1;

                    IF v_existente IS NOT NULL THEN
                        SELECT ARRAY(SELECT DISTINCT t FROM pg_catalog.unnest(i."TenantsAfectados" || v_tenants) AS t ORDER BY t)
                          INTO v_union
                          FROM public."IncidentesSeguridadSospechados" i WHERE i."Id" = v_existente;

                        UPDATE public."IncidentesSeguridadSospechados" i
                           SET "Detecciones" = i."Detecciones" + 1,
                               "UltimaDeteccionEnUtc" = pg_catalog.now(),
                               "TenantsAfectados" = v_union[1:50],
                               "Evidencia" = i."Evidencia"
                                   || CASE WHEN pg_catalog.cardinality(v_union) > 50
                                           THEN pg_catalog.jsonb_build_object('tenants_afectados_truncados', true)
                                           ELSE '{}'::jsonb END
                         WHERE i."Id" = v_existente;
                        RETURN QUERY SELECT v_existente, false;
                        RETURN;
                    END IF;

                    INSERT INTO public."IncidentesSeguridadSospechados"
                        ("Id", "Senal", "SujetoClave", "DetectadoEnUtc", "UltimaDeteccionEnUtc", "Detecciones",
                         "TenantsAfectados", "Evidencia", "Estado", "ConfirmadoEnUtc")
                    VALUES (p_incidente_id, p_senal, p_sujeto, pg_catalog.now(), pg_catalog.now(), 1,
                            v_tenants, v_evidencia, 'Sospechado', NULL);

                    INSERT INTO public."TransicionesIncidenteSeguridad"
                        ("Id", "IncidenteId", "EstadoAnterior", "EstadoNuevo", "OcurridoEnUtc", "ActorUsuarioId", "Motivo")
                    VALUES (p_transicion_id, p_incidente_id, NULL, 'Sospechado', pg_catalog.now(), NULL,
                            'Detectado por la señal ' || p_senal);

                    RETURN QUERY SELECT p_incidente_id, true;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_registrar_incidente_seguridad(uuid, uuid, text, text, uuid[], jsonb, integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_registrar_incidente_seguridad(uuid, uuid, text, text, uuid[], jsonb, integer)
                    TO cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION public.app_registrar_incidente_seguridad(uuid, uuid, text, text, uuid[], jsonb, integer);");

            migrationBuilder.DropTable(
                name: "TransicionesIncidenteSeguridad");

            migrationBuilder.DropTable(
                name: "IncidentesSeguridadSospechados");
        }
    }
}
