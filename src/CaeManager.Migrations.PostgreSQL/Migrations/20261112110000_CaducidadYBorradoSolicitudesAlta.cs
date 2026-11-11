using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Caducidad y borrado de datos personales de las Solicitudes de alta (A8, DA-7). <c>SolicitudesAlta</c> sigue
/// cerrada (RLS con FORCE, sin políticas, sin privilegios para los roles de la aplicación); el trabajo programado
/// llega a ella por dos funciones <c>SECURITY DEFINER</c> acotadas, una por operación, que ejecuta solo
/// <c>cae_app_runtime</c> y que devuelven únicamente un recuento:
/// <list type="bullet">
/// <item><c>app_alta_caducar_solicitudes</c> pasa a <c>Caducada</c> las Solicitudes de alta de <c>Iniciada</c>,
/// <c>CorreoVerificado</c>, <c>DatosCompletos</c> y <c>PendientePago</c> cuyo plazo (<c>CaducaEnUtc</c>, 7 días desde
/// <c>CreadaEnUtc</c>) ya venció según el reloj de la base. Espejo de <c>SolicitudAlta.Caducar</c>: nada que esté en
/// <c>Pagada</c>, <c>Completada</c>, <c>Reembolsada</c>, <c>DerivadaAAcceso</c> ni <c>Bloqueada</c>. Una Solicitud de
/// alta en <c>PendientePago</c> espera además una hora desde que entró en ese estado, porque su sesión de Checkout
/// dura 30 minutos y el webhook de un pago hecho a tiempo no podría registrarlo sobre una fila ya <c>Caducada</c>
/// (el dominio fija que lo pagado está pagado aunque el plazo haya vencido).</item>
/// <item><c>app_alta_borrar_datos_personales</c> borra los datos personales de las <c>Caducada</c> con 30 días
/// desde <c>CreadaEnUtc</c>: los mismos campos que <c>SolicitudAlta.BorrarDatosPersonales</c>, las filas de
/// <c>AceptacionesSolicitudAlta</c> y marca <c>DatosPersonalesBorradosEnUtc</c>. Repetirla no cambia nada, y deja
/// una fila en <c>TrazasPurga</c> con el recuento, como las demás purgas.</item>
/// </list>
/// Las dos trabajan por lotes (<c>p_lote</c> entre 1 y 1000) con <c>FOR UPDATE SKIP LOCKED</c>, así que ni se pisan con
/// el webhook de pago ni con otra instancia del trabajo, e incrementan <c>VersionFila</c> en cada fila que cambian.
/// Los plazos y el estado de origen están escritos dentro: el rol de la aplicación puede pedir el trabajo, pero no
/// adelantarlo ni elegir una fila. Como el resto de purgas no exigen el contexto firmado, porque no hay petición de
/// usuario detrás: las dispara el servicio de fondo de la plataforma.
/// </summary>
public partial class CaducidadYBorradoSolicitudesAlta : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $ampliacion$
            DECLARE
                v_nombre name;
            BEGIN
                FOR v_nombre IN
                    SELECT c.conname FROM pg_catalog.pg_constraint c
                     WHERE c.conrelid = 'public."TrazasPurga"'::regclass AND c.contype = 'c'
                       AND pg_catalog.pg_get_constraintdef(c.oid) LIKE '%IniciosSesion%'
                LOOP
                    EXECUTE pg_catalog.format('ALTER TABLE public."TrazasPurga" DROP CONSTRAINT %I', v_nombre);
                END LOOP;
            END
            $ampliacion$;
            ALTER TABLE public."TrazasPurga" ADD CONSTRAINT "CK_TrazasPurga_Purga"
                CHECK ("Purga" IN ('IniciosSesion', 'SesionesUsuario', 'RegistrosAuditoria', 'SolicitudesAlta'));

            CREATE FUNCTION public.app_alta_caducar_solicitudes(p_lote integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_ahora timestamp with time zone;
                v_filas integer;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_lote IS NULL OR p_lote < 1 OR p_lote > 1000 THEN
                    RAISE EXCEPTION 'El tamaño del lote tiene que estar entre 1 y 1000' USING ERRCODE = '22023';
                END IF;

                v_ahora := pg_catalog.clock_timestamp();

                -- SKIP LOCKED: una fila que el webhook de pago o otra instancia tiene bloqueada se deja para la
                -- siguiente pasada en vez de esperarla. Los plazos se comparan con el reloj de la base.
                WITH lote AS (
                    SELECT s."Id"
                      FROM public."SolicitudesAlta" s
                     WHERE s."CaducaEnUtc" <= v_ahora
                       AND (s."Estado" IN ('Iniciada', 'CorreoVerificado', 'DatosCompletos')
                            OR (s."Estado" = 'PendientePago'
                                AND s."UltimoCambioEstadoEnUtc" <= v_ahora - interval '1 hour'))
                     ORDER BY s."CaducaEnUtc", s."Id"
                     LIMIT p_lote
                       FOR UPDATE SKIP LOCKED)
                UPDATE public."SolicitudesAlta" s
                   SET "Estado" = 'Caducada',
                       "UltimoCambioEstadoEnUtc" = v_ahora,
                       "VersionFila" = s."VersionFila" + 1
                  FROM lote
                 WHERE s."Id" = lote."Id"
                   AND s."Estado" IN ('Iniciada', 'CorreoVerificado', 'DatosCompletos', 'PendientePago');
                GET DIAGNOSTICS v_filas = ROW_COUNT;
                RETURN v_filas;
            END;
            $$;

            CREATE FUNCTION public.app_alta_borrar_datos_personales(p_lote integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_ahora timestamp with time zone;
                v_corte timestamp with time zone;
                v_ids uuid[];
                v_filas integer;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_lote IS NULL OR p_lote < 1 OR p_lote > 1000 THEN
                    RAISE EXCEPTION 'El tamaño del lote tiene que estar entre 1 y 1000' USING ERRCODE = '22023';
                END IF;

                v_ahora := pg_catalog.clock_timestamp();
                v_corte := v_ahora - interval '30 days';

                -- Los Id se bloquean primero y las dos escrituras van sobre ese mismo conjunto: las aceptaciones
                -- se borran exactamente para las Solicitudes de alta que se limpian.
                SELECT pg_catalog.array_agg(x."Id") INTO v_ids
                  FROM (SELECT s."Id"
                          FROM public."SolicitudesAlta" s
                         WHERE s."Estado" = 'Caducada'
                           AND s."DatosPersonalesBorradosEnUtc" IS NULL
                           AND s."CreadaEnUtc" <= v_corte
                         ORDER BY s."CreadaEnUtc", s."Id"
                         LIMIT p_lote
                           FOR UPDATE SKIP LOCKED) x;
                IF v_ids IS NULL THEN
                    RETURN 0;
                END IF;

                DELETE FROM public."AceptacionesSolicitudAlta" a
                 WHERE a."SolicitudAltaId" = ANY (v_ids);

                UPDATE public."SolicitudesAlta" s
                   SET "Correo" = NULL,
                       "CorreoNormalizado" = NULL,
                       "HashContrasena" = NULL,
                       "HashTokenVerificacion" = NULL,
                       "TokenVerificacionCaducaEnUtc" = NULL,
                       "IpOrigen" = NULL,
                       "UserAgentOrigen" = NULL,
                       "RazonSocial" = NULL,
                       "CifNif" = NULL,
                       "DomicilioFiscal" = NULL,
                       "Telefono" = NULL,
                       "CheckoutSessionId" = NULL,
                       "DatosPersonalesBorradosEnUtc" = v_ahora,
                       "VersionFila" = s."VersionFila" + 1
                 WHERE s."Id" = ANY (v_ids)
                   AND s."Estado" = 'Caducada'
                   AND s."DatosPersonalesBorradosEnUtc" IS NULL;
                GET DIAGNOSTICS v_filas = ROW_COUNT;

                IF v_filas > 0 THEN
                    INSERT INTO public."TrazasPurga" ("Purga", "OcurridaEnUtc", "CorteUtc", "Filas", "Rol")
                    VALUES ('SolicitudesAlta', pg_catalog.clock_timestamp(), v_corte, v_filas,
                            CASE WHEN pg_catalog.current_setting('role') = 'cae_app_runtime'
                                       OR (pg_catalog.current_setting('role') = 'none' AND session_user = 'cae_app_runtime')
                                 THEN 'cae_app_runtime' ELSE 'otro' END);
                END IF;
                RETURN v_filas;
            END;
            $$;

            REVOKE ALL ON FUNCTION public.app_alta_caducar_solicitudes(integer) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_caducar_solicitudes(integer) TO cae_app_runtime;
            REVOKE ALL ON FUNCTION public.app_alta_borrar_datos_personales(integer) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_borrar_datos_personales(integer) TO cae_app_runtime;

            -- Con FORCE y sin políticas solo escribe quien evita la RLS: el propietario de cada función tiene que
            -- poder hacerlo, o el trabajo fallaría entero y más tarde, ya en producción.
            DO $comprobacion$
            DECLARE
                v_incumplen text;
            BEGIN
                SELECT string_agg(p.oid::regprocedure::text, ', ') INTO v_incumplen
                  FROM pg_catalog.pg_proc p
                  JOIN pg_catalog.pg_roles r ON r.oid = p.proowner
                 WHERE p.oid IN ('public.app_alta_caducar_solicitudes(integer)'::regprocedure,
                                 'public.app_alta_borrar_datos_personales(integer)'::regprocedure)
                   AND NOT ((r.rolsuper OR r.rolbypassrls)
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."SolicitudesAlta"', 'SELECT')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."SolicitudesAlta"', 'UPDATE')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."AceptacionesSolicitudAlta"', 'DELETE')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."TrazasPurga"', 'INSERT'));
                IF v_incumplen IS NOT NULL THEN
                    RAISE EXCEPTION 'SolicitudesAlta: el propietario de % no puede escribir la Solicitud de alta ni su traza', v_incumplen;
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
                IF EXISTS (SELECT 1 FROM public."TrazasPurga" WHERE "Purga" = 'SolicitudesAlta') THEN
                    RAISE EXCEPTION 'No se puede eliminar la evidencia de purgas de Solicitudes de alta';
                END IF;
            END $$;
            DROP FUNCTION public.app_alta_borrar_datos_personales(integer);
            DROP FUNCTION public.app_alta_caducar_solicitudes(integer);
            ALTER TABLE public."TrazasPurga" DROP CONSTRAINT "CK_TrazasPurga_Purga";
            ALTER TABLE public."TrazasPurga" ADD CONSTRAINT "TrazasPurga_Purga_check"
                CHECK ("Purga" IN ('IniciosSesion', 'SesionesUsuario', 'RegistrosAuditoria'));
            """);
    }
}
