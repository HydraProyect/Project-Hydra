using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RegistraActividadSoportePorSesionPrivilegiada : Migration
    {
        /// <summary>Firma de la función, la misma en el GRANT, el REVOKE y el DROP.</summary>
        internal const string Firma = "public.app_registrar_actividad_soporte_por_sesion(uuid, uuid, text, text)";

        /// <summary>
        /// Los únicos detalles que la función admite: el nombre de cada request marcado
        /// <c>ILecturaRegistradaEnSesionPrivilegiada</c> en el momento de esta migración. Es una
        /// copia a propósito, no una referencia a Application: una migración aplicada no cambia.
        /// <c>RequestsDelImportadorRegistranLecturaPrivilegiadaTests</c> exige que la lista de la
        /// migración vigente coincida con los tipos marcados, así que marcar un request nuevo
        /// obliga a una migración nueva que la amplíe.
        /// </summary>
        internal static readonly string[] OperacionesRegistradas =
        [
            "AnalizarImportacionExcelQuery",
            "AnalizarPlantillaClientesQuery",
            "AnalizarPlantillaCombinadaQuery",
            "AnalizarPlantillaDocumentosQuery",
            "AnalizarPlantillaMaestraQuery",
            "EjecutarImportacionCombinadaCommand",
            "EjecutarImportacionCommand",
            "EjecutarImportacionMaestraCommand",
            "ObtenerHistorialImportacionesQuery",
            "ObtenerNombreCuentaQuery",
            "PrevisualizarImportacionMaestraCommand",
        ];

        private static string ListaSql(string[] valores) =>
            string.Join(", ", System.Array.ConvertAll(valores, v => "'" + v + "'"));

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #2001 (decisión de Chris del 2026-11-03, al cerrar la #1971): toda lectura que un
            // Actor de Plataforma TALVEG hace bajo una Sesión Privilegiada deja una fila en
            // RegistrosActividadSoporte. Esa conexión adopta cae_app_soporte, que solo tiene SELECT
            // sobre la tabla y no lo pierde: esta función es su única puerta de escritura, igual que
            // app_registrar_acceso_documento_sensible_por_soporte para los documentos sensibles.
            //
            // Se acota sola, con lo que la conexión no puede elegir:
            //   - Contexto: el contexto RLS firmado (app_ctx_valido). Actor real = app_ctx_usuario_id(),
            //     Tenant = app_ctx_tenant_id(); sin contexto válido no escribe nada.
            //   - Sesión: la que se nombra, abierta (CerradaEnUtc nulo), dentro de su ventana, con el
            //     Tenant objetivo igual al Tenant del contexto, y con una concesión vigente cuyo actor
            //     es el actor real del contexto y que todavía alcanza ese Tenant (global, o con el
            //     Tenant en TenantsAlcanzadosPorConcesion). Las mismas condiciones que
            //     SesionPrivilegiadaActual comprueba en la aplicación. No se filtra por capacidad:
            //     registrar una lectura no concede nada.
            //   - Unicidad: el contexto firmado no lleva la Sesión Privilegiada seleccionada (añadirla
            //     cambiaría el token y app_contexto_validado, que leen todas las políticas RLS). Con dos
            //     Sesiones Privilegiadas vigentes del mismo Actor real sobre el mismo Tenant, la fila se
            //     atribuye a la que declara la petición; ambas son válidas por construcción
            //     (comportamiento aceptado). Una sesión de otro actor, de otro Tenant, caducada o cerrada
            //     no registra.
            //   - Fila: TenantId (el Tenant objetivo), UsuarioSoporteId (el actor real, nunca el
            //     usuario simulado), SesionPrivilegiadaId y OcurridaEnUtc los pone la función;
            //     DelegacionTenantId queda nulo (CK_RegistrosActividadSoporte_UnSoloAgrupador). Del
            //     llamador solo vienen el Id del registro (UUID v7 de la aplicación), el tipo (solo
            //     'Lectura') y el detalle, que solo puede ser uno de los nombres de request de
            //     OperacionesRegistradas (lista cerrada): nunca texto libre ni datos leídos.
            //   - Hora: clock_timestamp(), no now(), tomada justo al validar la sesión. now() es la hora
            //     de inicio de la transacción, y una transacción abierta antes de que la sesión o la
            //     concesión caducaran seguiría viéndolas vigentes; la misma hora fecha la fila.
            //   - Aislamiento: solo READ COMMITTED, donde cada sentencia toma una instantánea nueva. Bajo
            //     REPEATABLE READ o SERIALIZABLE la función vería la sesión y la concesión como estaban
            //     al empezar la transacción, aunque después se cerraran o revocaran: rechaza sin insertar.
            //     Transacción: la fila solo es duradera en autocommit, y la guarda no está aquí sino en el
            //     repositorio (rechaza con una transacción de EF o ambiente abierta) más el trinquete
            //     SinTransaccionesAbiertasPorSqlCrudoTests (ninguna cadena de src abre una transacción
            //     con BEGIN o START TRANSACTION). No se hace en SQL porque la función no puede distinguir
            //     una transacción READ COMMITTED explícita del autocommit: con el protocolo extendido de
            //     Npgsql, statement_timestamp() se fija en cada mensaje Parse/Bind/Execute y
            //     transaction_timestamp() en el primero, así que no coinciden tampoco en autocommit.
            // El propietario de la función es el rol de las migraciones: por eso el TenantId de la
            // fila sale del contexto firmado y no de un parámetro. Devuelve un código en vez de lanzar,
            // para que el repositorio falle en cerrado con el motivo en el registro. EXECUTE solo para
            // cae_app_soporte; los GRANT de la tabla no cambian.
            migrationBuilder.Sql(
                $"""
                CREATE FUNCTION public.app_registrar_actividad_soporte_por_sesion(
                    p_sesion uuid, p_registro uuid, p_tipo text, p_detalle text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant uuid;
                  v_ahora timestamptz;
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL THEN
                    RETURN 'contexto_no_valido';
                  END IF;

                  IF current_setting('transaction_isolation') <> 'read committed' THEN
                    RETURN 'aislamiento_no_admitido';
                  END IF;

                  IF p_registro IS NULL OR p_tipo IS NULL OR p_tipo NOT IN ('Lectura')
                     OR p_detalle IS NULL OR p_detalle NOT IN ({ListaSql(OperacionesRegistradas)}) THEN
                    RETURN 'valor_no_valido';
                  END IF;

                  -- La sesión nombrada: abierta, en su ventana, sobre el Tenant del contexto y con una
                  -- concesión vigente del actor real que alcanza ese Tenant. La hora se toma aquí.
                  v_ahora := clock_timestamp();
                  PERFORM 1
                  FROM public."SesionesPrivilegiadas" s
                  JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND s."TenantObjetivoId" = v_tenant
                    AND s."CerradaEnUtc" IS NULL
                    AND s."InicioEnUtc" <= v_ahora
                    AND s."ExpiraEnUtc" > v_ahora
                    AND c."UsuarioPlataformaId" = v_actor
                    AND c."Estado" = 'Vigente'
                    AND c."VigenciaDesde" <= v_ahora
                    AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora)
                    AND (c."EsAlcanceGlobal" OR EXISTS (
                        SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                        WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant));
                  IF NOT FOUND THEN
                    RETURN 'sesion_no_autorizada';
                  END IF;

                  INSERT INTO public."RegistrosActividadSoporte" (
                      "Id", "UsuarioSoporteId", "Tipo", "DelegacionTenantId", "Detalle", "OcurridaEnUtc",
                      "TenantId", "SesionPrivilegiadaId")
                  VALUES (
                      p_registro, v_actor, p_tipo, NULL, p_detalle, v_ahora,
                      v_tenant, p_sesion);

                  RETURN 'registrado';
                END;
                $$;

                REVOKE ALL ON FUNCTION {Firma} FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION {Firma} TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                DROP FUNCTION {Firma};
                """);
        }
    }
}
