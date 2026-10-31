using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PurgaAuditoriaYPlazosDeConservacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FechaBajaUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Cuentas ya desactivadas: su fecha de baja real no consta, así que el plazo de 12 meses
            // cuenta desde aquí. Nunca antes de lo debido. Mismo criterio que
            // ApplicationUser.BloqueoDesactivaLaCuenta (un bloqueo de más de 365 días).
            migrationBuilder.Sql(
                """
                UPDATE public."AspNetUsers"
                   SET "FechaBajaUtc" = pg_catalog.now()
                 WHERE "LockoutEnd" > pg_catalog.now() + interval '365 days';
                """);

            // Plazos decididos el 2026-10-31. Seguridad: 12 meses, también para las sesiones.
            // Auditoría: 5 años. Mismo contrato que app_purgar_inicios_sesion: SECURITY DEFINER,
            // su propietario es la identidad explícita que cruza Tenants, corte fijo dentro y el
            // runtime solo con EXECUTE. El parámetro elige entre contar y borrar, nunca el corte.
            // Evidencia de aceptación: el contrato y 5 años. Sin fecha de fin de contrato no se
            // purga; lo que sí se hace es imponer en base de datos que no se edita ni se borra.
            // AuditoriasExtraccionIa: el runtime la actualiza al registrar la decisión humana, pero ya no
            // la borra, y la purga respeta la fila de una revisión de IA aún sin resolver.
            migrationBuilder.Sql(
                """
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
                     WHERE "EmitidaEnUtc" < pg_catalog.now() - interval '5 years';
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;

                CREATE FUNCTION public.app_purgar_registros_auditoria(p_simular boolean) RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_corte timestamp with time zone := pg_catalog.now() - interval '4 years';
                    v_filas integer;
                    v_total integer := 0;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de auditoría no vería ninguna fila', current_user;
                    END IF;

                    IF p_simular THEN
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
                     WHERE a."CreadaEnUtc" < v_corte;
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    v_total := v_total + v_filas;
                    RETURN v_total;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_purgar_registros_auditoria(boolean) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_purgar_registros_auditoria(boolean) TO cae_app_runtime;

                
                
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT UPDATE, DELETE ON TABLE public."AceptacionesTerminos" TO cae_app_runtime;
                GRANT DELETE ON TABLE public."AuditoriasExtraccionIa" TO cae_app_runtime;

                DROP FUNCTION public.app_purgar_registros_auditoria(boolean);

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
                     WHERE "EmitidaEnUtc" < pg_catalog.now() - interval '5 years';
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;
                """);

            migrationBuilder.DropColumn(
                name: "FechaBajaUtc",
                table: "AspNetUsers");
        }
    }
}
