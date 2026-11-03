using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

public partial class TrazaPropiaDePurgas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Registro transversal de plataforma, sin identificadores personales ni Tenant propietario.
        // Solo las funciones SECURITY DEFINER escriben; los roles de aplicación no acceden.
        // No se fija aquí un plazo de conservación para esta nueva evidencia.
        // Con FORCE y sin políticas solo escribe quien evita la RLS: el INSERT corre como propietario de
        // cada función, que CREATE OR REPLACE conserva y puede no ser el rol que crea la tabla. La
        // comprobación final lo exige aquí, porque si no la purga fallaría entera y más tarde.
        migrationBuilder.Sql("""
            CREATE TABLE public."TrazasPurga" (
                "Purga" text NOT NULL CHECK ("Purga" IN ('IniciosSesion', 'SesionesUsuario', 'RegistrosAuditoria')),
                "OcurridaEnUtc" timestamp with time zone NOT NULL,
                "CorteUtc" timestamp with time zone NOT NULL,
                "Filas" integer NOT NULL CHECK ("Filas" > 0),
                "Rol" text NOT NULL CHECK ("Rol" IN ('cae_app_runtime', 'otro'))
            );
            ALTER TABLE public."TrazasPurga" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public."TrazasPurga" FORCE ROW LEVEL SECURITY;
            REVOKE ALL ON TABLE public."TrazasPurga" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;

            CREATE OR REPLACE FUNCTION public.app_purgar_inicios_sesion() RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_corte timestamp with time zone := pg_catalog.now() - interval '12 months';
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de inicios de sesión no vería ninguna fila', current_user;
                    END IF;
                    DELETE FROM public."RegistrosInicioSesion"
                     WHERE "OcurridoEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    IF v_filas > 0 THEN
                        INSERT INTO public."TrazasPurga" ("Purga", "OcurridaEnUtc", "CorteUtc", "Filas", "Rol")
                        VALUES ('IniciosSesion', pg_catalog.clock_timestamp(), v_corte, v_filas,
                                CASE WHEN pg_catalog.current_setting('role') = 'cae_app_runtime'
                                           OR (pg_catalog.current_setting('role') = 'none' AND session_user = 'cae_app_runtime')
                                     THEN 'cae_app_runtime' ELSE 'otro' END);
                    END IF;
                    RETURN v_filas;
                END;
                $$;

            CREATE OR REPLACE FUNCTION public.app_purgar_sesiones_usuario() RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_corte timestamp with time zone := pg_catalog.now() - interval '12 months';
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de sesiones no vería ninguna fila', current_user;
                    END IF;
                    DELETE FROM public."SesionesUsuario"
                     WHERE "EmitidaEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    IF v_filas > 0 THEN
                        INSERT INTO public."TrazasPurga" ("Purga", "OcurridaEnUtc", "CorteUtc", "Filas", "Rol")
                        VALUES ('SesionesUsuario', pg_catalog.clock_timestamp(), v_corte, v_filas,
                                CASE WHEN pg_catalog.current_setting('role') = 'cae_app_runtime'
                                           OR (pg_catalog.current_setting('role') = 'none' AND session_user = 'cae_app_runtime')
                                     THEN 'cae_app_runtime' ELSE 'otro' END);
                    END IF;
                    RETURN v_filas;
                END;
                $$;

            CREATE OR REPLACE FUNCTION public.app_purgar_registros_auditoria(p_simular boolean) RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_corte timestamp with time zone := pg_catalog.now() - interval '5 years';
                    v_filas integer;
                    v_total integer := 0;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de auditoría no vería ninguna fila', current_user;
                    END IF;

                    IF p_simular IS NOT FALSE THEN
                        SELECT (SELECT count(*) FROM public."RegistrosAuditoria" WHERE "FechaUtc" < v_corte)
                             + (SELECT count(*) FROM public."RegistrosAccesoDocumentoSensible" WHERE "OcurridoEnUtc" < v_corte)
                             + (SELECT count(*) FROM public."RegistrosActividadSoporte" WHERE "OcurridaEnUtc" < v_corte)
                             + (SELECT count(*) FROM public."AuditoriasExtraccionIa" a
                                 WHERE a."CreadaEnUtc" < v_corte
                                   AND NOT EXISTS (SELECT 1 FROM public."RevisionesIaDocumento" r
                                                    WHERE r."AuditoriaExtraccionIaId" = a."Id" AND NOT r."Resuelta"))
                          INTO v_total;
                        RETURN v_total;
                    END IF;

                    DELETE FROM public."RegistrosAuditoria" WHERE "FechaUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    DELETE FROM public."RegistrosAccesoDocumentoSensible" WHERE "OcurridoEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    DELETE FROM public."RegistrosActividadSoporte" WHERE "OcurridaEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    DELETE FROM public."AuditoriasExtraccionIa" a
                     WHERE a."CreadaEnUtc" < v_corte
                       AND NOT EXISTS (SELECT 1 FROM public."RevisionesIaDocumento" r
                                        WHERE r."AuditoriaExtraccionIaId" = a."Id" AND NOT r."Resuelta");
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    IF v_total > 0 THEN
                        INSERT INTO public."TrazasPurga" ("Purga", "OcurridaEnUtc", "CorteUtc", "Filas", "Rol")
                        VALUES ('RegistrosAuditoria', pg_catalog.clock_timestamp(), v_corte, v_total,
                                CASE WHEN pg_catalog.current_setting('role') = 'cae_app_runtime'
                                           OR (pg_catalog.current_setting('role') = 'none' AND session_user = 'cae_app_runtime')
                                     THEN 'cae_app_runtime' ELSE 'otro' END);
                    END IF;
                    RETURN v_total;
                END;
                $$;

            DO $comprobacion$
            DECLARE
                v_incumplen text;
            BEGIN
                SELECT string_agg(p.oid::regprocedure::text, ', ') INTO v_incumplen
                  FROM pg_catalog.pg_proc p
                  JOIN pg_catalog.pg_roles r ON r.oid = p.proowner
                 WHERE p.oid IN ('public.app_purgar_inicios_sesion()'::regprocedure,
                                 'public.app_purgar_sesiones_usuario()'::regprocedure,
                                 'public.app_purgar_registros_auditoria(boolean)'::regprocedure)
                   AND NOT ((r.rolsuper OR r.rolbypassrls)
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."TrazasPurga"', 'INSERT'));
                IF v_incumplen IS NOT NULL THEN
                    RAISE EXCEPTION 'TrazasPurga: el propietario de % no puede escribir la traza y la purga fallaría', v_incumplen;
                END IF;
            END
            $comprobacion$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // No descartar silenciosamente evidencia ya generada al revertir la migración.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM public."TrazasPurga") THEN
                    RAISE EXCEPTION 'No se puede eliminar la evidencia de purgas';
                END IF;
            END $$;
            CREATE OR REPLACE FUNCTION public.app_purgar_inicios_sesion() RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de inicios de sesión no vería ninguna fila', current_user;
                    END IF;
                    DELETE FROM public."RegistrosInicioSesion"
                     WHERE "OcurridoEnUtc" < pg_catalog.now() - interval '12 months';
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;

            CREATE OR REPLACE FUNCTION public.app_purgar_sesiones_usuario() RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de sesiones no vería ninguna fila', current_user;
                    END IF;
                    DELETE FROM public."SesionesUsuario"
                     WHERE "EmitidaEnUtc" < pg_catalog.now() - interval '12 months';
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;

            CREATE OR REPLACE FUNCTION public.app_purgar_registros_auditoria(p_simular boolean) RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_corte timestamp with time zone := pg_catalog.now() - interval '5 years';
                    v_filas integer;
                    v_total integer := 0;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de auditoría no vería ninguna fila', current_user;
                    END IF;

                    IF p_simular IS NOT FALSE THEN
                        SELECT (SELECT count(*) FROM public."RegistrosAuditoria" WHERE "FechaUtc" < v_corte)
                             + (SELECT count(*) FROM public."RegistrosAccesoDocumentoSensible" WHERE "OcurridoEnUtc" < v_corte)
                             + (SELECT count(*) FROM public."RegistrosActividadSoporte" WHERE "OcurridaEnUtc" < v_corte)
                             + (SELECT count(*) FROM public."AuditoriasExtraccionIa" a
                                 WHERE a."CreadaEnUtc" < v_corte
                                   AND NOT EXISTS (SELECT 1 FROM public."RevisionesIaDocumento" r
                                                    WHERE r."AuditoriaExtraccionIaId" = a."Id" AND NOT r."Resuelta"))
                          INTO v_total;
                        RETURN v_total;
                    END IF;

                    DELETE FROM public."RegistrosAuditoria" WHERE "FechaUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    DELETE FROM public."RegistrosAccesoDocumentoSensible" WHERE "OcurridoEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    DELETE FROM public."RegistrosActividadSoporte" WHERE "OcurridaEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    DELETE FROM public."AuditoriasExtraccionIa" a
                     WHERE a."CreadaEnUtc" < v_corte
                       AND NOT EXISTS (SELECT 1 FROM public."RevisionesIaDocumento" r
                                        WHERE r."AuditoriaExtraccionIaId" = a."Id" AND NOT r."Resuelta");
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    RETURN v_total;
                END;
                $$;
            DROP TABLE public."TrazasPurga";
            """);
    }
}
