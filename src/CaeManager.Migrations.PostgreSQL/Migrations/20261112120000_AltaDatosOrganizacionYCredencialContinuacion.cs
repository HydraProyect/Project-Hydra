using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Paso «Empresa» del alta autónoma (A4) y pre-secuestro de la verificación del correo. <c>SolicitudesAlta</c> sigue
/// cerrada (RLS con FORCE, sin políticas, sin privilegios para los roles de la aplicación): todo pasa por funciones
/// <c>SECURITY DEFINER</c> que solo ejecuta <c>cae_app_runtime</c> y que exigen el contexto RLS firmado.
/// <list type="bullet">
/// <item>Columnas <c>HashCredencialContinuacion</c> (SHA-256 del credencial de 256 bits que deja seguir al paso «Empresa»
/// sin cuenta ni sesión) y <c>CredencialContinuacionCaducaEnUtc</c>, con índice único filtrado.</item>
/// <item><c>app_alta_consultar_enlace</c>: con el token vigente y la Solicitud de alta en <c>Iniciada</c>, devuelve el
/// hash de la contraseña para que la aplicación la compare con el hasher de Identity; a nadie más.</item>
/// <item><c>app_alta_verificar_correo</c> pasa a tres argumentos: bajo candado exige que el hash comparado siga siendo
/// el guardado y guarda el hash del credencial de continuación. La firma de un argumento se conserva para la ranura
/// anterior durante el relevo, pero siempre responde <c>NoValido</c>; se elimina en una migración posterior.</item>
/// <item><c>app_alta_completar_datos_organizacion</c>: con el credencial, guarda los datos y pasa a
/// <c>DatosCompletos</c> o, si el CIF ya está en <c>CifsPropietarioTenant</c> o es el de una Empresa propia de algún
/// Tenant, a <c>DerivadaAAcceso</c>. Devuelve solo el resultado y el Id de la propia Solicitud de alta.</item>
/// <item><c>app_alta_consultar_continuacion</c>: el estado de la continuación (sin escribir), para recargar o reenviar
/// el paso sin volver a transicionar.</item>
/// <item><c>app_alta_caducar_solicitudes</c> y <c>app_alta_borrar_datos_personales</c> (de A8) vacían también
/// <c>HashCredencialContinuacion</c> y <c>CredencialContinuacionCaducaEnUtc</c>, como <c>SolicitudAlta.Caducar</c> (un
/// estado final deja sin credencial) y <c>SolicitudAlta.BorrarDatosPersonales</c>. Down les devuelve el cuerpo de A8 antes
/// de quitar las columnas.</item>
/// </list>
/// </summary>
public partial class AltaDatosOrganizacionYCredencialContinuacion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CredencialContinuacionCaducaEnUtc",
            table: "SolicitudesAlta",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "HashCredencialContinuacion",
            table: "SolicitudesAlta",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SolicitudesAlta_HashCredencialContinuacion",
            table: "SolicitudesAlta",
            column: "HashCredencialContinuacion",
            unique: true,
            filter: "\"HashCredencialContinuacion\" IS NOT NULL");

        migrationBuilder.Sql("""
            -- Relevo de ranuras: la versión anterior desplegada (A3) sigue llamando a la verificación de un argumento
            -- mientras conviven las dos ranuras, así que la firma se conserva. Ya no verifica nada: verificar sin comprobar
            -- la contraseña reabriría el pre-secuestro de la cuenta. Se elimina en una migración posterior, cuando no quede
            -- ninguna ranura con el código de A3. Conserva propietario y privilegios (CREATE OR REPLACE).
            CREATE OR REPLACE FUNCTION public.app_alta_verificar_correo(p_hash_token text) RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                RETURN 'NoValido';
            END;
            $$;

            -- Primer tiempo de la verificación (A4, pre-secuestro): con el SHA-256 del token del enlace, dice si el
            -- enlace vale y, solo si vale y la Solicitud de alta sigue en Iniciada, devuelve el hash de la contraseña para
            -- que la aplicación la compare con PasswordHasher. El hash nunca sale hacia el navegador; quien no presenta un
            -- token vigente no obtiene nada. No bloquea ni escribe: el segundo tiempo vuelve a comprobarlo todo bajo candado.
            CREATE FUNCTION public.app_alta_consultar_enlace(p_hash_token text)
                RETURNS TABLE(resultado text, hash_contrasena text)
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_estado text;
                v_token_caduca timestamp with time zone;
                v_caduca timestamp with time zone;
                v_hash text;
                v_ahora timestamp with time zone;
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_hash_token IS NULL OR p_hash_token !~ '^[0-9a-f]{64}$' THEN
                    RETURN QUERY SELECT 'NoValido'::text, NULL::text;
                    RETURN;
                END IF;

                SELECT s."Estado", s."TokenVerificacionCaducaEnUtc", s."CaducaEnUtc", s."HashContrasena"
                  INTO v_estado, v_token_caduca, v_caduca, v_hash
                  FROM public."SolicitudesAlta" s
                 WHERE s."HashTokenVerificacion" = p_hash_token;

                v_ahora := pg_catalog.clock_timestamp();

                IF NOT FOUND OR v_estado <> 'Iniciada' OR v_hash IS NULL THEN
                    RETURN QUERY SELECT 'NoValido'::text, NULL::text;
                ELSIF v_ahora >= v_caduca THEN
                    RETURN QUERY SELECT 'SolicitudCaducada'::text, NULL::text;
                ELSIF v_token_caduca IS NULL OR v_ahora >= v_token_caduca THEN
                    RETURN QUERY SELECT 'EnlaceCaducado'::text, NULL::text;
                ELSE
                    RETURN QUERY SELECT 'Vigente'::text, v_hash;
                END IF;
            END;
            $$;

            -- Segundo tiempo: la aplicación ya comprobó la contraseña contra el hash que le dio el primero. Bajo candado,
            -- con el reloj leído después, repite las comprobaciones del enlace y exige que el hash de la contraseña sea el
            -- mismo que se comparó (si cambió entre los dos tiempos, no verifica). Verificado, el enlace se gasta y se guarda
            -- el SHA-256 del credencial de continuación, que vive dos horas como mucho y nunca más que la Solicitud de alta.
            CREATE FUNCTION public.app_alta_verificar_correo(p_hash_token text, p_hash_contrasena text, p_hash_credencial text)
                RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_id uuid;
                v_estado text;
                v_token_caduca timestamp with time zone;
                v_caduca timestamp with time zone;
                v_hash text;
                v_ahora timestamp with time zone;
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_hash_token IS NULL OR p_hash_token !~ '^[0-9a-f]{64}$' OR p_hash_contrasena IS NULL THEN
                    RETURN 'NoValido';
                END IF;
                IF p_hash_credencial IS NULL OR p_hash_credencial !~ '^[0-9a-f]{64}$' THEN
                    RAISE EXCEPTION 'El credencial de continuación tiene que llegar como SHA-256 en hexadecimal' USING ERRCODE = '22023';
                END IF;

                -- FOR UPDATE: dos envíos a la vez sobre el mismo enlace se ordenan, y el segundo ya no encuentra el hash.
                SELECT s."Id", s."Estado", s."TokenVerificacionCaducaEnUtc", s."CaducaEnUtc", s."HashContrasena"
                  INTO v_id, v_estado, v_token_caduca, v_caduca, v_hash
                  FROM public."SolicitudesAlta" s
                 WHERE s."HashTokenVerificacion" = p_hash_token
                   FOR UPDATE;

                -- El reloj se lee con el bloqueo ya tomado: una petición que esperó a otra no verifica con la hora de antes
                -- de la espera.
                v_ahora := pg_catalog.clock_timestamp();

                IF NOT FOUND OR v_estado <> 'Iniciada' OR v_hash IS DISTINCT FROM p_hash_contrasena THEN
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
                       "HashCredencialContinuacion" = p_hash_credencial,
                       "CredencialContinuacionCaducaEnUtc" = LEAST(v_ahora + interval '2 hours', v_caduca),
                       "UltimoCambioEstadoEnUtc" = v_ahora,
                       "VersionFila" = "VersionFila" + 1
                 WHERE "Id" = v_id;
                RETURN 'Verificado';
            END;
            $$;

            -- Paso «Empresa» (A4): con el SHA-256 del credencial de continuación, guarda los datos de la organización y
            -- decide bajo candado si el CIF ya tiene Tenant propietario. Devuelve solo el resultado y el Id de la propia
            -- Solicitud de alta (para el aviso a Soporte TALVEG): nada del Tenant, ni su Id, ni su nombre, ni quién lo
            -- gestiona. Otra Solicitud de alta con el mismo CIF no cuenta aquí: la reserva del CIF es de A6.
            -- El CIF está ocupado si está en CifsPropietarioTenant o si es el de una Empresa propia (EsPropia, no
            -- eliminada) de cualquier Tenant: los Tenants creados antes de A1 no tienen fila en CifsPropietarioTenant, y
            -- sin esto su CIF pasaría por libre (DM-5). Empresas."Cif" se guardó con Trim y mayúsculas, no con
            -- ValidadorIdentificacion.Normalizar: aquí se reduce a ASCII alfanumérico en mayúsculas y se repone el cero
            -- de un DNI de siete dígitos. Es un superconjunto seguro de Normalizar, que quita todo char.IsWhiteSpace
            -- (también U+00A0), puntos y guiones: un CIF o NIF válido normalizado solo tiene [0-9A-Z] (lo exige el
            -- techo de p_cif_nif de arriba). No usa [[:space:]] ni upper() ni rangos, que dependen del locale y de la
            -- intercalación de la base: translate y la lista explícita de caracteres no.
            CREATE FUNCTION public.app_alta_completar_datos_organizacion(
                    p_hash_credencial text, p_razon_social text, p_cif_nif text, p_domicilio_fiscal text, p_telefono text,
                    p_tramo text, p_declara_representacion boolean, p_declara_actividad_profesional boolean)
                RETURNS TABLE(resultado text, solicitud_alta_id uuid)
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_id uuid;
                v_estado text;
                v_caduca timestamp with time zone;
                v_credencial_caduca timestamp with time zone;
                v_ahora timestamp with time zone;
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_hash_credencial IS NULL OR p_hash_credencial !~ '^[0-9a-f]{64}$' THEN
                    RETURN QUERY SELECT 'NoValido'::text, NULL::uuid;
                    RETURN;
                END IF;
                -- La aplicación ya validó con las reglas del dominio (DatosOrganizacionAlta); esto es el techo de la base.
                IF p_razon_social IS NULL OR pg_catalog.length(p_razon_social) NOT BETWEEN 1 AND 200
                   OR p_domicilio_fiscal IS NULL OR pg_catalog.length(p_domicilio_fiscal) NOT BETWEEN 1 AND 500
                   OR p_cif_nif IS NULL OR p_cif_nif !~ '^[0-9A-Z]{1,20}$'
                   OR (p_telefono IS NOT NULL AND (pg_catalog.length(p_telefono) > 30 OR p_telefono !~ '^[0-9 +().-]+$'))
                   OR p_tramo IS NULL OR p_tramo NOT IN ('De1A10', 'De11A40', 'De41A80', 'De81A150', 'De151A300')
                   OR p_declara_representacion IS DISTINCT FROM true
                   OR p_declara_actividad_profesional IS NULL
                   OR (p_cif_nif ~ '^([0-9]{8}|[XYZ][0-9]{7})[A-Z]$' AND NOT p_declara_actividad_profesional) THEN
                    RAISE EXCEPTION 'Datos de la organización fuera de rango' USING ERRCODE = '22023';
                END IF;

                -- FOR UPDATE: dos envíos a la vez con el mismo credencial se ordenan, y el segundo ya no la encuentra en
                -- CorreoVerificado.
                SELECT s."Id", s."Estado", s."CaducaEnUtc", s."CredencialContinuacionCaducaEnUtc"
                  INTO v_id, v_estado, v_caduca, v_credencial_caduca
                  FROM public."SolicitudesAlta" s
                 WHERE s."HashCredencialContinuacion" = p_hash_credencial
                   FOR UPDATE;

                v_ahora := pg_catalog.clock_timestamp();

                IF NOT FOUND OR v_estado <> 'CorreoVerificado' OR v_ahora >= v_caduca
                   OR v_credencial_caduca IS NULL OR v_ahora >= v_credencial_caduca THEN
                    RETURN QUERY SELECT 'NoValido'::text, NULL::uuid;
                    RETURN;
                END IF;

                IF EXISTS (SELECT 1 FROM public."CifsPropietarioTenant" c WHERE c."Cif" = p_cif_nif)
                   OR EXISTS (SELECT 1
                                FROM public."Empresas" e
                                CROSS JOIN LATERAL (SELECT pg_catalog.regexp_replace(
                                                       pg_catalog.translate(e."Cif", 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
                                                       '[^0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ]', '', 'g') AS limpio) n
                               WHERE e."EsPropia" AND NOT e."EstaEliminado" AND e."Cif" IS NOT NULL
                                 AND (CASE WHEN n.limpio ~ '^[0-9]{7}[A-Z]$' THEN '0' || n.limpio ELSE n.limpio END) = p_cif_nif) THEN
                    -- Estado final: se guarda el CIF para la petición de acceso y el credencial deja de servir.
                    UPDATE public."SolicitudesAlta"
                       SET "Estado" = 'DerivadaAAcceso',
                           "CifNif" = p_cif_nif,
                           "HashCredencialContinuacion" = NULL,
                           "CredencialContinuacionCaducaEnUtc" = NULL,
                           "UltimoCambioEstadoEnUtc" = v_ahora,
                           "VersionFila" = "VersionFila" + 1
                     WHERE "Id" = v_id;
                    RETURN QUERY SELECT 'DerivadaAAcceso'::text, v_id;
                    RETURN;
                END IF;

                UPDATE public."SolicitudesAlta"
                   SET "Estado" = 'DatosCompletos',
                       "RazonSocial" = p_razon_social,
                       "CifNif" = p_cif_nif,
                       "DomicilioFiscal" = p_domicilio_fiscal,
                       "Telefono" = p_telefono,
                       "Tramo" = p_tramo,
                       "DeclaraRepresentacion" = true,
                       "DeclaraActividadProfesional" = p_declara_actividad_profesional,
                       "UltimoCambioEstadoEnUtc" = v_ahora,
                       "VersionFila" = "VersionFila" + 1
                 WHERE "Id" = v_id;
                RETURN QUERY SELECT 'DatosCompletos'::text, v_id;
            END;
            $$;

            -- En qué punto está la Solicitud de alta de un credencial de continuación, para que la página del paso
            -- «Empresa» pinte el formulario, lo ya guardado o el aviso de volver a empezar al recargar o reenviar. No
            -- bloquea ni escribe, y devuelve solo el estado: ningún dato de la Solicitud de alta ni del Tenant.
            CREATE FUNCTION public.app_alta_consultar_continuacion(p_hash_credencial text)
                RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_estado text;
                v_caduca timestamp with time zone;
                v_credencial_caduca timestamp with time zone;
                v_ahora timestamp with time zone;
            BEGIN
                PERFORM public.app_exigir_contexto_firmado();
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: no vería ninguna Solicitud de alta', current_user;
                END IF;
                IF p_hash_credencial IS NULL OR p_hash_credencial !~ '^[0-9a-f]{64}$' THEN
                    RETURN 'NoValido';
                END IF;

                SELECT s."Estado", s."CaducaEnUtc", s."CredencialContinuacionCaducaEnUtc"
                  INTO v_estado, v_caduca, v_credencial_caduca
                  FROM public."SolicitudesAlta" s
                 WHERE s."HashCredencialContinuacion" = p_hash_credencial;
                IF NOT FOUND THEN
                    RETURN 'NoValido';
                END IF;

                v_ahora := pg_catalog.clock_timestamp();
                IF v_credencial_caduca IS NULL OR v_ahora >= v_credencial_caduca OR v_ahora >= v_caduca THEN
                    RETURN 'Caducada';
                END IF;
                IF v_estado = 'CorreoVerificado' THEN
                    RETURN 'CorreoVerificado';
                END IF;
                IF v_estado = 'DatosCompletos' THEN
                    RETURN 'DatosCompletos';
                END IF;
                RETURN 'NoValido';
            END;
            $$;

            -- La caducidad de A8 vacía también el credencial de continuación y su caducidad al pasar a Caducada, como
            -- SolicitudAlta.CambiarEstado al entrar en un estado final: el credencial deja de servir en la misma sentencia.
            -- Es el cuerpo de A8 con esas dos columnas más; propietario y privilegios se conservan (CREATE OR REPLACE) y
            -- se reafirman abajo. Down devuelve el de A8 antes de quitar las columnas.
            CREATE OR REPLACE FUNCTION public.app_alta_caducar_solicitudes(p_lote integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                SET TimeZone TO 'UTC'
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

                -- SKIP LOCKED: una fila que otra transacción tiene bloqueada (otra instancia, la verificación del
                -- correo) se deja para la siguiente pasada en vez de esperarla. Los plazos se comparan con el reloj
                -- de la base. PendientePago espera 96 horas desde que entró: ver el comentario de la migración.
                -- Los plazos son horas exactas, no días naturales (con otra zona horaria un cambio de hora daría
                -- 95 o 97 horas), y la función fija TimeZone a UTC. El plazo de 168 horas desde la creación está
                -- escrito aquí además de en CaducaEnUtc: app_alta_iniciar_solicitud admite un CaducaEnUtc posterior
                -- a la creación pero inferior a siete días, y esa fila no puede caducar antes de plazo.
                WITH lote AS (
                    SELECT s."Id"
                      FROM public."SolicitudesAlta" s
                     WHERE s."CaducaEnUtc" <= v_ahora
                       AND s."CreadaEnUtc" <= v_ahora - interval '168 hours'
                       AND (s."Estado" IN ('Iniciada', 'CorreoVerificado', 'DatosCompletos')
                            OR (s."Estado" = 'PendientePago'
                                AND s."UltimoCambioEstadoEnUtc" <= v_ahora - interval '96 hours'))
                     ORDER BY s."CaducaEnUtc", s."Id"
                     LIMIT p_lote
                       FOR UPDATE SKIP LOCKED)
                UPDATE public."SolicitudesAlta" s
                   SET "Estado" = 'Caducada',
                       "HashCredencialContinuacion" = NULL,
                       "CredencialContinuacionCaducaEnUtc" = NULL,
                       "UltimoCambioEstadoEnUtc" = v_ahora,
                       "VersionFila" = s."VersionFila" + 1
                  FROM lote
                 WHERE s."Id" = lote."Id"
                   AND s."Estado" IN ('Iniciada', 'CorreoVerificado', 'DatosCompletos', 'PendientePago');
                GET DIAGNOSTICS v_filas = ROW_COUNT;
                RETURN v_filas;
            END;
            $$;

            REVOKE ALL ON FUNCTION public.app_alta_caducar_solicitudes(integer) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_caducar_solicitudes(integer) TO cae_app_runtime;

            -- El borrado de datos personales de A8 (DA-7) vacía también el credencial de continuación y su caducidad,
            -- como SolicitudAlta.BorrarDatosPersonales: sin esto una Caducada con los datos borrados conservaría el hash.
            -- Es el cuerpo de A8 con esas dos columnas más; propietario y privilegios se conservan (CREATE OR REPLACE) y
            -- se reafirman abajo. Down devuelve el de A8 antes de quitar las columnas.
            CREATE OR REPLACE FUNCTION public.app_alta_borrar_datos_personales(p_lote integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                SET TimeZone TO 'UTC'
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
                v_corte := v_ahora - interval '720 hours';

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
                       "HashCredencialContinuacion" = NULL,
                       "CredencialContinuacionCaducaEnUtc" = NULL,
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

            REVOKE ALL ON FUNCTION public.app_alta_borrar_datos_personales(integer) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_borrar_datos_personales(integer) TO cae_app_runtime;
            REVOKE ALL ON FUNCTION public.app_alta_consultar_continuacion(text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_consultar_continuacion(text) TO cae_app_runtime;
            REVOKE ALL ON FUNCTION public.app_alta_consultar_enlace(text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_consultar_enlace(text) TO cae_app_runtime;
            REVOKE ALL ON FUNCTION public.app_alta_verificar_correo(text, text, text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_verificar_correo(text, text, text) TO cae_app_runtime;
            REVOKE ALL ON FUNCTION public.app_alta_completar_datos_organizacion(text, text, text, text, text, text, boolean, boolean)
                FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_completar_datos_organizacion(text, text, text, text, text, text, boolean, boolean)
                TO cae_app_runtime;

            -- Con FORCE y sin políticas solo lee y escribe quien evita la RLS: el propietario de cada función tiene que
            -- poder hacerlo, o el alta fallaría más tarde, ya en una petición.
            DO $comprobacion$
            DECLARE
                v_incumplen text;
            BEGIN
                SELECT string_agg(p.oid::regprocedure::text, ', ') INTO v_incumplen
                  FROM pg_catalog.pg_proc p
                  JOIN pg_catalog.pg_roles r ON r.oid = p.proowner
                 WHERE p.oid IN ('public.app_alta_consultar_enlace(text)'::regprocedure,
                                 'public.app_alta_verificar_correo(text, text, text)'::regprocedure,
                                 'public.app_alta_completar_datos_organizacion(text, text, text, text, text, text, boolean, boolean)'::regprocedure,
                                 'public.app_alta_consultar_continuacion(text)'::regprocedure)
                   AND NOT ((r.rolsuper OR r.rolbypassrls)
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."SolicitudesAlta"', 'SELECT')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."SolicitudesAlta"', 'UPDATE')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."CifsPropietarioTenant"', 'SELECT')
                            AND pg_catalog.has_table_privilege(p.proowner, 'public."Empresas"', 'SELECT'));
                IF v_incumplen IS NOT NULL THEN
                    RAISE EXCEPTION 'SolicitudesAlta: el propietario de % no puede leer o escribir la Solicitud de alta', v_incumplen;
                END IF;
            END
            $comprobacion$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- Antes de quitar las columnas del credencial, la caducidad y el borrado de datos personales vuelven al cuerpo
            -- exacto de A8, que no las nombra: si no, la primera pasada del trabajo de limpieza fallaría por columna
            -- inexistente.
            CREATE OR REPLACE FUNCTION public.app_alta_caducar_solicitudes(p_lote integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                SET TimeZone TO 'UTC'
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

                -- SKIP LOCKED: una fila que otra transacción tiene bloqueada (otra instancia, la verificación del
                -- correo) se deja para la siguiente pasada en vez de esperarla. Los plazos se comparan con el reloj
                -- de la base. PendientePago espera 96 horas desde que entró: ver el comentario de la migración.
                -- Los plazos son horas exactas, no días naturales (con otra zona horaria un cambio de hora daría
                -- 95 o 97 horas), y la función fija TimeZone a UTC. El plazo de 168 horas desde la creación está
                -- escrito aquí además de en CaducaEnUtc: app_alta_iniciar_solicitud admite un CaducaEnUtc posterior
                -- a la creación pero inferior a siete días, y esa fila no puede caducar antes de plazo.
                WITH lote AS (
                    SELECT s."Id"
                      FROM public."SolicitudesAlta" s
                     WHERE s."CaducaEnUtc" <= v_ahora
                       AND s."CreadaEnUtc" <= v_ahora - interval '168 hours'
                       AND (s."Estado" IN ('Iniciada', 'CorreoVerificado', 'DatosCompletos')
                            OR (s."Estado" = 'PendientePago'
                                AND s."UltimoCambioEstadoEnUtc" <= v_ahora - interval '96 hours'))
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
            REVOKE ALL ON FUNCTION public.app_alta_caducar_solicitudes(integer) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_caducar_solicitudes(integer) TO cae_app_runtime;

            CREATE OR REPLACE FUNCTION public.app_alta_borrar_datos_personales(p_lote integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                SET TimeZone TO 'UTC'
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
                v_corte := v_ahora - interval '720 hours';

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
            REVOKE ALL ON FUNCTION public.app_alta_borrar_datos_personales(integer) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_borrar_datos_personales(integer) TO cae_app_runtime;

            DROP FUNCTION public.app_alta_consultar_continuacion(text);
            DROP FUNCTION public.app_alta_completar_datos_organizacion(text, text, text, text, text, text, boolean, boolean);
            DROP FUNCTION public.app_alta_verificar_correo(text, text, text);
            DROP FUNCTION public.app_alta_consultar_enlace(text);
            -- Up conservó la firma de un argumento con un cuerpo que no verifica: se le devuelve el de A3.
            CREATE OR REPLACE FUNCTION public.app_alta_verificar_correo(p_hash_token text) RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_id uuid;
                v_estado text;
                v_token_caduca timestamp with time zone;
                v_caduca timestamp with time zone;
                v_ahora timestamp with time zone;
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

                -- El reloj se lee con el bloqueo ya tomado: una petición que esperó a otra no verifica con la hora de antes
                -- de la espera, cuando el enlace o la Solicitud de alta quizá ya han caducado.
                v_ahora := pg_catalog.clock_timestamp();

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
            REVOKE ALL ON FUNCTION public.app_alta_verificar_correo(text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT EXECUTE ON FUNCTION public.app_alta_verificar_correo(text) TO cae_app_runtime;
            """);

        migrationBuilder.DropIndex(
            name: "IX_SolicitudesAlta_HashCredencialContinuacion",
            table: "SolicitudesAlta");

        migrationBuilder.DropColumn(
            name: "CredencialContinuacionCaducaEnUtc",
            table: "SolicitudesAlta");

        migrationBuilder.DropColumn(
            name: "HashCredencialContinuacion",
            table: "SolicitudesAlta");
    }
}
