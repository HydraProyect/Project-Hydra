using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Vía de acceso del registro anónimo del alta autónoma (A3) a <c>SolicitudesAlta</c>, que sigue cerrada: RLS con
/// FORCE, sin políticas y sin privilegios para los roles de la aplicación (A1). En vez de abrir la tabla a
/// <c>cae_app_runtime</c> con una política, dos funciones <c>SECURITY DEFINER</c> acotadas hacen cada una una sola
/// cosa, como <c>TrazasPurga</c>:
/// <list type="bullet">
/// <item><c>app_alta_iniciar_solicitud</c> inserta una Solicitud de alta en <c>Iniciada</c>. No puede crearla en otro
/// estado ni con más datos que los del registro, y no devuelve nada.</item>
/// <item><c>app_alta_verificar_correo</c> recibe el SHA-256 del token del enlace, pasa la Solicitud de alta a
/// <c>CorreoVerificado</c> si el enlace vale y responde solo con el resultado. Es el espejo de
/// <c>SolicitudAlta.VerificarCorreo</c>: un solo uso (borra el hash), caducidad del enlace y de la Solicitud de alta
/// con el reloj de la base, y nada fuera de <c>Iniciada</c>.</item>
/// </list>
/// Las dos exigen el contexto RLS firmado (<c>app_exigir_contexto_firmado</c>), igual que el resto de resoluciones
/// anónimas, y solo las ejecuta <c>cae_app_runtime</c>. Ninguna lee ni devuelve el hash de la contraseña.
/// </summary>
public partial class AccesoSolicitudesAltaPorFunciones : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION public.app_alta_iniciar_solicitud(
                    p_id uuid, p_correo text, p_correo_normalizado text, p_hash_contrasena text,
                    p_hash_token text, p_token_caduca_en timestamp with time zone, p_ip text, p_user_agent text,
                    p_creada_en timestamp with time zone, p_caduca_en timestamp with time zone) RETURNS void
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no podría registrar la Solicitud de alta', current_user;
                END IF;
                IF p_id IS NULL OR p_correo IS NULL OR p_correo_normalizado IS NULL OR p_hash_contrasena IS NULL
                   OR p_ip IS NULL OR p_creada_en IS NULL THEN
                    RAISE EXCEPTION 'Faltan datos del registro' USING ERRCODE = '22023';
                END IF;
                IF p_hash_token IS NULL OR p_hash_token !~ '^[0-9a-f]{64}$' THEN
                    RAISE EXCEPTION 'El token de verificación tiene que llegar como SHA-256 en hexadecimal' USING ERRCODE = '22023';
                END IF;
                -- Mismos plazos que el dominio como techo: el enlace dura hasta 24 h y la Solicitud de alta hasta 7 días.
                IF p_creada_en < pg_catalog.now() - interval '5 minutes' OR p_creada_en > pg_catalog.now() + interval '5 minutes'
                   OR p_token_caduca_en IS NULL OR p_token_caduca_en <= p_creada_en OR p_token_caduca_en > p_creada_en + interval '24 hours'
                   OR p_caduca_en IS NULL OR p_caduca_en <= p_creada_en OR p_caduca_en > p_creada_en + interval '168 hours' THEN
                    RAISE EXCEPTION 'Plazos del registro fuera de rango' USING ERRCODE = '22023';
                END IF;

                INSERT INTO public."SolicitudesAlta" (
                    "Id", "Estado", "Correo", "CorreoNormalizado", "HashContrasena", "HashTokenVerificacion",
                    "TokenVerificacionCaducaEnUtc", "IpOrigen", "UserAgentOrigen", "CreadaEnUtc", "CaducaEnUtc",
                    "UltimoCambioEstadoEnUtc", "DeclaraRepresentacion", "DeclaraActividadProfesional", "VersionFila")
                VALUES (
                    p_id, 'Iniciada', p_correo, p_correo_normalizado, p_hash_contrasena, p_hash_token,
                    p_token_caduca_en, p_ip, p_user_agent, p_creada_en, p_caduca_en,
                    p_creada_en, false, false, 0);
            END;
            $$;

            CREATE FUNCTION public.app_alta_verificar_correo(p_hash_token text) RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_id uuid;
                v_estado text;
                v_token_caduca timestamp with time zone;
                v_caduca timestamp with time zone;
                v_ahora timestamp with time zone := pg_catalog.clock_timestamp();
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_hash_token IS NULL OR p_hash_token !~ '^[0-9a-f]{64}$' THEN
                    RETURN 'NoValido';
                END IF;

                -- FOR UPDATE: dos clics a la vez sobre el mismo enlace se ordenan, y el segundo ya no encuentra el hash.
                SELECT s."Id", s."Estado", s."TokenVerificacionCaducaEnUtc", s."CaducaEnUtc"
                  INTO v_id, v_estado, v_token_caduca, v_caduca
                  FROM public."SolicitudesAlta" s
                 WHERE s."HashTokenVerificacion" = p_hash_token
                   FOR UPDATE;

                IF NOT FOUND OR v_estado <> 'Iniciada' THEN
                    RETURN 'NoValido';
                END IF;
                IF v_ahora >= v_caduca THEN
                    RETURN 'SolicitudCaducada';
                END IF;
                IF v_token_caduca IS NULL OR v_ahora >= v_token_caduca THEN
                    RETURN 'EnlaceCaducado';
                END IF;

                UPDATE public."SolicitudesAlta"
                   SET "Estado" = 'CorreoVerificado',
                       "HashTokenVerificacion" = NULL,
                       "TokenVerificacionCaducaEnUtc" = NULL,
                       "CorreoVerificadoEnUtc" = v_ahora,
                       "UltimoCambioEstadoEnUtc" = v_ahora,
                       "VersionFila" = "VersionFila" + 1
                 WHERE "Id" = v_id;
                RETURN 'Verificado';
            END;
            $$;

            REVOKE ALL ON FUNCTION public.app_alta_iniciar_solicitud(uuid, text, text, text, text, timestamp with time zone, text, text, timestamp with time zone, timestamp with time zone)
                FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_iniciar_solicitud(uuid, text, text, text, text, timestamp with time zone, text, text, timestamp with time zone, timestamp with time zone)
                TO cae_app_runtime;
            REVOKE ALL ON FUNCTION public.app_alta_verificar_correo(text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_verificar_correo(text) TO cae_app_runtime;

            -- Con FORCE y sin políticas solo escribe quien evita la RLS: el propietario de cada función tiene que
            -- poder hacerlo, o el registro fallaría entero y más tarde, ya en una petición.
            DO $comprobacion$
            DECLARE
                v_incumplen text;
            BEGIN
                SELECT string_agg(p.oid::regprocedure::text, ', ') INTO v_incumplen
                  FROM pg_catalog.pg_proc p
                  JOIN pg_catalog.pg_roles r ON r.oid = p.proowner
                 WHERE p.oid IN ('public.app_alta_iniciar_solicitud(uuid, text, text, text, text, timestamp with time zone, text, text, timestamp with time zone, timestamp with time zone)'::regprocedure,
                                 'public.app_alta_verificar_correo(text)'::regprocedure)
                   AND NOT ((r.rolsuper OR r.rolbypassrls)
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."SolicitudesAlta"', 'INSERT')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."SolicitudesAlta"', 'UPDATE'));
                IF v_incumplen IS NOT NULL THEN
                    RAISE EXCEPTION 'SolicitudesAlta: el propietario de % no puede escribir la Solicitud de alta', v_incumplen;
                END IF;
            END
            $comprobacion$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION public.app_alta_verificar_correo(text);
            DROP FUNCTION public.app_alta_iniciar_solicitud(uuid, text, text, text, text, timestamp with time zone, text, text, timestamp with time zone, timestamp with time zone);
            """);
    }
}
