using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Fase de contracción de la procedencia de la Asignación de Operación Outbound externa (PR #2237; decisión de Chris
    /// del 2026-11-15, opción c: corte en dos PR; opción A de Chris del 2026-11-14: reactivar una DelegacionTenant nunca
    /// repone nada). La fase de expansión, <c>AnadeOrigenDeOperacionExterna</c> (PR #2253), ya añadió las columnas
    /// <c>AsignacionesOperacion.OrigenId</c>, <c>RevocacionesDelOrigen</c>, <c>TransaccionDeAlta</c> e
    /// <c>InicioTransaccionDeAlta</c> y <c>DelegacionesTenant.Revocaciones</c>, con su FK, su índice y los triggers
    /// <c>delegacion_tenant_cuenta_revocaciones</c>, <c>asignacion_operacion_sella_origen</c>,
    /// <c>asignacion_operacion_conserva_su_alta</c> y <c>operacion_externa_sella_origen_de_su_transaccion</c>. Esta
    /// migración no los vuelve a crear: solo contrae y hace el backfill. Los lectores de alcance pasan a
    /// <c>AmparoDeOrigen.AmparadasPorSuOrigen</c>, que falla cerrado sin origen, y <c>AsignacionesOperativasBackfillSeeder</c>
    /// cierra en cada arranque, con sus carteras, toda operación Outbound externa no cerrada que siga sin origen.
    /// <para>
    /// Lo que añade:
    /// <list type="bullet">
    /// <item><c>delegacion_tenant_cuenta_revocaciones</c> rechaza además todo cambio de <c>Proposito</c>: una delegación de
    /// Operador CAE externo que pasara a Soporte saldría sin contar la revocación ni revocar sus filas, y al volver
    /// conservaría el amparo (ronda 3 de Codex, alta 2; ningún escritor del código cambia el propósito).</item>
    /// <item><c>delegacion_tenant_revoca_filas_heredadas</c>: cada revocación contada de una delegación de Operador CAE
    /// externo revoca sus <c>AsignacionesOperadorDelegado</c> no revocadas. La fila heredada autoriza por sí sola (vía
    /// heredada de <c>TenantsBeneficiariosAutorizados</c>) y, si sobreviviera, volvería a autorizar al reactivar la
    /// delegación (ronda 1 de Codex, alta 1).</item>
    /// <item><c>asignacion_operador_delegado_exige_delegacion_activa</c>: una fila heredada viva no nace sobre una
    /// delegación de Operador CAE externo inactiva; si el escritor trae el contador que leyó al decidir
    /// (<c>AsignacionesOperadorDelegado.RevocacionesDelOrigen</c>, columna nueva), se rechaza si ya no es el de la
    /// delegación, y en todo caso se sella con el real (ronda 3 de Codex, alta 1).
    /// <c>asignacion_operador_delegado_revocacion_definitiva</c>: una fila revocada no se des-revoca y ninguna fila cambia
    /// de delegación, de persona, de rol ni de contador (ronda 2 de Codex, alta 2).</item>
    /// <item><c>asignacion_operacion_sella_origen</c> pasa a dispararse también en el UPDATE: el origen y su contador no
    /// cambian nunca, y una operación con origen no cambia de par ni de servicio. La única excepción es el sellado de
    /// <c>operacion_externa_sella_origen_de_su_transaccion</c>, que fija de NULL a un valor el origen de una operación
    /// Outbound externa nacida en ESTA transacción (equivale a sellarla al insertar, y pasa por la misma comprobación
    /// que un origen explícito): cubre el orden de guardado de la ranura anterior, que durante este despliegue es la de la
    /// PR #2253.</item>
    /// <item><c>TR_EncargosAdministracion_ExigeAmparoDeOrigen</c>: un Encargo de Administración solo nace sobre una
    /// operación externa que su origen ampara al insertarlo, con la delegación bloqueada (ronda 3 de Codex, media 3), y
    /// <c>app_operacion_amparada_por_su_origen(uuid)</c>, el amparo para quien no lee <c>DelegacionesTenant</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Reloj de pared en las caducidades</b> (ronda 4 de Codex, media 2). <c>now()</c> es el inicio de la transacción:
    /// una inserción que empieza antes de que caduque la delegación y llega al trigger después (por ejemplo, tras
    /// esperar un bloqueo) la daría por vigente. Las comprobaciones de caducidad de los triggers y de la función del
    /// amparo usan <c>clock_timestamp()</c>, leído después de tomar el bloqueo. <c>InicioTransaccionDeAlta</c> sigue
    /// siendo <c>now()</c>: identifica la transacción, no compara una caducidad.
    /// </para>
    /// <para>
    /// <b>Orden de bloqueo frente a la revocación por Soporte TALVEG</b> (ronda 4 de Codex, media 1). La clave foránea del
    /// Encargo toma <c>FOR KEY SHARE</c> sobre la Asignación de Operación antes de que el trigger del Encargo pida
    /// <c>FOR SHARE</c> sobre la DelegacionTenant; <c>app_revocar_operador_cae_externo_por_soporte</c> bloqueaba la
    /// delegación y después las operaciones con <c>FOR UPDATE</c>, que choca con <c>FOR KEY SHARE</c>: interbloqueo. La
    /// función se reescribe con <c>FOR NO KEY UPDATE</c> sobre las operaciones, el mismo bloqueo que toma su propio
    /// UPDATE (no cambia ninguna clave): ya no espera a la clave foránea del Encargo, confirma, y el Encargo, que
    /// esperaba la delegación, la encuentra revocada y se rechaza. El resto de la función no cambia.
    /// </para>
    /// <para>
    /// Lo que existía antes de la fase de expansión no tiene historia de revocaciones: el contador nació a cero para
    /// todas, y la reactivación de una delegación comercial no guarda fecha. Por eso nada anterior puede probar que
    /// nació bajo la activación vigente de su delegación, y falla cerrado (decisión de la coordinadora en la PR #2237, la
    /// data actual es de prueba): esta migración revoca las filas heredadas no revocadas de las delegaciones de Operador
    /// CAE externo (<see cref="RevocaFilasHeredadasPrevias"/>). Nunca fija el origen de una operación ya existente ni
    /// crea operaciones externas: solo las abre un acto explícito.
    /// </para>
    /// <para>
    /// Las funciones de los triggers no son SECURITY DEFINER, salvo dos: <see cref="CreaFuncionAmparoDeOrigen"/>, que
    /// recibe solo el Id de una operación y solo responde un booleano, y la función del trigger de
    /// <see cref="CreaExigenciaDeAmparoDelEncargo"/>, que no se puede llamar fuera del trigger y solo rechaza: el Encargo
    /// de Administración por Aprovisionamiento lo escribe <c>cae_app_aprovisionamiento</c>, que no lee ni bloquea
    /// <c>DelegacionesTenant</c>. Ninguna concede a ningún rol lectura ni escritura nueva.
    /// </para>
    /// </summary>
    public partial class AnadeProcedenciaDeOperacionExterna : Migration
    {
        /// <summary>
        /// Revoca, una sola vez, las filas heredadas no revocadas de toda DelegacionTenant de Operador CAE externo: sin
        /// historia de revocaciones previa a la fase de expansión, ninguna puede probar que no sobrevivió a una. Público
        /// para que la suite de integración lo ejerza sobre datos sembrados.
        /// </summary>
        public const string RevocaFilasHeredadasPrevias =
            """
            UPDATE public."AsignacionesOperadorDelegado" a
               SET "RevocadaEnUtc" = pg_catalog.now(),
                   "MotivoRevocacion" = 'Sin historia de revocaciones de su DelegacionTenant: no prueba que no sobreviviera a una.'
             WHERE a."RevocadaEnUtc" IS NULL
               AND EXISTS (SELECT 1 FROM public."DelegacionesTenant" d
                            WHERE d."Id" = a."DelegacionTenantId" AND d."Proposito" = 'OperadorExterno');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RevocacionesDelOrigen",
                table: "AsignacionesOperadorDelegado",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(RevocaFilasHeredadasPrevias);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_delegacion_tenant_cuenta_revocaciones() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        NEW."Revocaciones" := 0;
                        RETURN NEW;
                    END IF;

                    -- El propósito no cambia nunca (ronda 3 de Codex, alta 2): pasar por otro propósito dejaría una
                    -- revocación sin revocar las filas heredadas, o una ampliación de caducidad sin contar.
                    IF NEW."Proposito" IS DISTINCT FROM OLD."Proposito" THEN
                        RAISE EXCEPTION 'El propósito de una DelegacionTenant es inmutable (%).', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF (OLD."Activa" AND NOT NEW."Activa")
                       OR ((OLD."Proposito" = 'OperadorExterno' OR NEW."Proposito" = 'OperadorExterno')
                           AND OLD."Activa" AND NEW."Activa"
                           AND OLD."ExpiraEnUtc" IS NOT NULL
                           AND (NEW."ExpiraEnUtc" IS NULL OR NEW."ExpiraEnUtc" > OLD."ExpiraEnUtc")) THEN
                        NEW."Revocaciones" := OLD."Revocaciones" + 1;
                    ELSE
                        NEW."Revocaciones" := OLD."Revocaciones";
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION public.app_delegacion_tenant_revoca_filas_heredadas() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF (OLD."Proposito" = 'OperadorExterno' OR NEW."Proposito" = 'OperadorExterno')
                       AND NEW."Revocaciones" > OLD."Revocaciones" THEN
                        UPDATE public."AsignacionesOperadorDelegado"
                           SET "RevocadaEnUtc" = pg_catalog.now(),
                               "MotivoRevocacion" = 'Revocada su DelegacionTenant: reactivarla no la repone.'
                         WHERE "DelegacionTenantId" = NEW."Id"
                           AND "RevocadaEnUtc" IS NULL;
                    END IF;
                    RETURN NULL;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_delegacion_tenant_revoca_filas_heredadas() FROM PUBLIC;
                CREATE TRIGGER delegacion_tenant_revoca_filas_heredadas
                    AFTER UPDATE ON public."DelegacionesTenant"
                    FOR EACH ROW EXECUTE FUNCTION public.app_delegacion_tenant_revoca_filas_heredadas();

                -- La revocación de arriba es una foto: una fila heredada que un escritor inserte después de leer la
                -- delegación activa, pero confirmada tras la revocación, sobreviviría y volvería a autorizar al
                -- reactivar. Con FOR SHARE sobre la delegación, la revocación espera a este INSERT (y su trigger AFTER la
                -- revoca) o este INSERT espera a la revocación, relee la fila confirmada y se rechaza.
                -- Activa no basta (ronda 3 de Codex, alta 1): una ampliación de caducidad, o una revocación y una
                -- reactivación completas, entre la decisión del escritor y su INSERT dejan la delegación activa con otra
                -- activación. El escritor que decide sobre una lectura previa (el catálogo de carteras, con el contador
                -- sellado de la operación amparada; el alta de una persona y las siembras, con el de la delegación que
                -- leyeron) trae ese contador en RevocacionesDelOrigen, y si ya no es el de la delegación la inserción se
                -- rechaza, como en asignacion_operacion_sella_origen. Sin él (la ranura anterior) se comprueba solo
                -- Activa bajo el FOR SHARE. En los dos casos la fila queda sellada con el contador vigente al nacer.
                CREATE FUNCTION public.app_asignacion_operador_delegado_exige_delegacion_activa() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_proposito text;
                    v_activa boolean;
                    v_revocaciones integer;
                BEGIN
                    IF NEW."RevocadaEnUtc" IS NOT NULL THEN
                        RETURN NEW;
                    END IF;
                    SELECT d."Proposito", d."Activa", d."Revocaciones" INTO v_proposito, v_activa, v_revocaciones
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."DelegacionTenantId"
                       FOR SHARE;
                    IF NOT FOUND THEN
                        -- Invisible bajo la RLS de quien escribe: lo rechaza el WITH CHECK de la tabla.
                        RETURN NEW;
                    END IF;
                    IF v_proposito = 'OperadorExterno' AND NOT v_activa THEN
                        RAISE EXCEPTION 'Una fila heredada no nace sobre una DelegacionTenant de Operador CAE externo revocada (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW."RevocacionesDelOrigen" IS NOT NULL AND NEW."RevocacionesDelOrigen" <> v_revocaciones THEN
                        RAISE EXCEPTION 'La DelegacionTenant de la fila heredada se revocó o se amplió después de decidirla (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    NEW."RevocacionesDelOrigen" := v_revocaciones;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operador_delegado_exige_delegacion_activa() FROM PUBLIC;
                CREATE TRIGGER asignacion_operador_delegado_exige_delegacion_activa
                    BEFORE INSERT ON public."AsignacionesOperadorDelegado"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operador_delegado_exige_delegacion_activa();

                -- Revocar una fila heredada es definitivo: un UPDATE que devolviera RevocadaEnUtc a NULL la devolvería al
                -- lector de autorización, y al reactivar la delegación recuperaría su efecto. Tampoco cambia de qué
                -- delegación, de qué persona ni con qué rol es: una fila no se recicla para otra cosa.
                CREATE FUNCTION public.app_asignacion_operador_delegado_revocacion_definitiva() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NEW."Id" IS DISTINCT FROM OLD."Id"
                       OR NEW."DelegacionTenantId" IS DISTINCT FROM OLD."DelegacionTenantId"
                       OR NEW."UsuarioId" IS DISTINCT FROM OLD."UsuarioId"
                       OR NEW."Rol" IS DISTINCT FROM OLD."Rol"
                       OR NEW."CreadoEnUtc" IS DISTINCT FROM OLD."CreadoEnUtc"
                       OR NEW."RevocacionesDelOrigen" IS DISTINCT FROM OLD."RevocacionesDelOrigen" THEN
                        RAISE EXCEPTION 'Una fila heredada no cambia de delegación, de persona ni de rol (%).', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF OLD."RevocadaEnUtc" IS NOT NULL
                       AND (NEW."RevocadaEnUtc" IS DISTINCT FROM OLD."RevocadaEnUtc"
                            OR NEW."MotivoRevocacion" IS DISTINCT FROM OLD."MotivoRevocacion") THEN
                        RAISE EXCEPTION 'La revocación de una fila heredada es definitiva (%).', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operador_delegado_revocacion_definitiva() FROM PUBLIC;
                CREATE TRIGGER asignacion_operador_delegado_revocacion_definitiva
                    BEFORE UPDATE ON public."AsignacionesOperadorDelegado"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operador_delegado_revocacion_definitiva();
                """);

            migrationBuilder.Sql(SellaOrigenContraccion);
            migrationBuilder.Sql(
                """
                DROP TRIGGER asignacion_operacion_sella_origen ON public."AsignacionesOperacion";
                CREATE TRIGGER asignacion_operacion_sella_origen
                    BEFORE INSERT OR UPDATE ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_sella_origen();
                """);
            migrationBuilder.Sql(SellaOrigenDeSuTransaccion.Replace(Reloj, "pg_catalog.clock_timestamp()"));

            migrationBuilder.Sql(CreaFuncionAmparoDeOrigen);
            migrationBuilder.Sql(CreaExigenciaDeAmparoDelEncargo);
            migrationBuilder.Sql(RevocacionPorSoporte.Replace(BloqueoDeOperaciones, "FOR NO KEY UPDATE"));
        }

        private const string Reloj = "__RELOJ__";
        private const string BloqueoDeOperaciones = "__BLOQUEO_DE_OPERACIONES__";

        /// <summary>
        /// <c>app_asignacion_operacion_sella_origen</c> en la contracción: la inserción de la fase de expansión, con la
        /// caducidad comparada con <c>clock_timestamp()</c>, más la rama UPDATE que hace inmutables el origen y su
        /// contador (rondas 1 a 3 de Codex en la PR #2237) salvo el sellado en la transacción de alta.
        /// </summary>
        private const string SellaOrigenContraccion =
            """
            CREATE OR REPLACE FUNCTION public.app_asignacion_operacion_sella_origen() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_revocaciones integer;
                v_activa boolean;
                v_expira public."DelegacionesTenant"."ExpiraEnUtc"%TYPE;
                v_vistas text;
                v_bloqueadas text;
                v_activas bigint;
                v_unica uuid;
                v_ahora timestamp with time zone;
            BEGIN
                IF TG_OP = 'UPDATE' THEN
                    IF NEW."OrigenId" IS NOT DISTINCT FROM OLD."OrigenId"
                       AND NEW."RevocacionesDelOrigen" IS NOT DISTINCT FROM OLD."RevocacionesDelOrigen" THEN
                        -- Una operación con origen no cambia de par ni de servicio (convertirla en interna borraría el
                        -- sello, y otro par no es el de su delegación).
                        IF OLD."OrigenId" IS NOT NULL
                           AND (NEW."PropietarioTenantId" IS DISTINCT FROM OLD."PropietarioTenantId"
                                OR NEW."OperadorTenantId" IS DISTINCT FROM OLD."OperadorTenantId"
                                OR NEW."Servicio" IS DISTINCT FROM OLD."Servicio") THEN
                            RAISE EXCEPTION 'Una Asignación de Operación con origen no cambia de par ni de servicio (%).', NEW."Id"
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    -- El origen y su contador solo se fijan al nacer y no cambian nunca: sellar después repondría una
                    -- operación superviviente. La única excepción es el sellado, de NULL a un valor, de una operación
                    -- Outbound externa nacida en ESTA transacción (lo hace operacion_externa_sella_origen_de_su_transaccion
                    -- para el orden de guardado de la ranura anterior): equivale a sellarla al insertar, y pasa abajo
                    -- por la misma comprobación que un origen explícito.
                    IF NOT (OLD."OrigenId" IS NULL AND OLD."RevocacionesDelOrigen" IS NULL
                            AND NEW."OrigenId" IS NOT NULL
                            AND OLD."PropietarioTenantId" <> OLD."OperadorTenantId"
                            AND OLD."Servicio" = 'Outbound'
                            AND NEW."PropietarioTenantId" = OLD."PropietarioTenantId"
                            AND NEW."OperadorTenantId" = OLD."OperadorTenantId"
                            AND NEW."Servicio" = OLD."Servicio"
                            AND OLD."TransaccionDeAlta" = pg_catalog.pg_current_xact_id()
                            AND OLD."InicioTransaccionDeAlta" = pg_catalog.now()) THEN
                        RAISE EXCEPTION 'La DelegacionTenant de origen de una Asignación de Operación es inmutable (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                ELSE
                    -- La transacción que inserta la operación, siempre y pise lo que venga: la lee el sellado de
                    -- operacion_externa_sella_origen_de_su_transaccion.
                    NEW."TransaccionDeAlta" := pg_catalog.pg_current_xact_id();
                    NEW."InicioTransaccionDeAlta" := pg_catalog.now();

                    -- Una Asignación de Operación interna no nace de ninguna DelegacionTenant.
                    IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" THEN
                        NEW."OrigenId" := NULL;
                        NEW."RevocacionesDelOrigen" := NULL;
                        RETURN NEW;
                    END IF;

                    IF NEW."OrigenId" IS NULL THEN
                        NEW."RevocacionesDelOrigen" := NULL;
                        IF NEW."Servicio" <> 'Outbound' THEN
                            RETURN NEW;
                        END IF;

                        -- Inserción sin origen (la ranura anterior no lo trae). Primero, las candidatas que ve esta
                        -- sentencia sin bloquear nada.
                        v_ahora := pg_catalog.clock_timestamp();
                        SELECT pg_catalog.string_agg(d."Id"::text || ':' || d."Revocaciones"::text, ',' ORDER BY d."Id")
                          INTO v_vistas
                          FROM public."DelegacionesTenant" d
                         WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                           AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                           AND d."Proposito" = 'OperadorExterno'
                           AND d."Activa"
                           AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > v_ahora);

                        -- Después, FOR SHARE sobre TODAS las OperadorExterno del par, activas o no, hasta el final de la
                        -- transacción, y se decide sobre las filas ya bloqueadas con el reloj leído tras el bloqueo
                        -- (si una caduca entre las dos lecturas, no coinciden y se reintenta). La condición de activa va
                        -- en el FILTER de cada agregado, no en el WHERE del bloqueo (revisión puente de la ronda 2 de la
                        -- PR #2253).
                        PERFORM 1
                           FROM public."DelegacionesTenant" d
                          WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                            AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                            AND d."Proposito" = 'OperadorExterno'
                            FOR SHARE;
                        v_ahora := pg_catalog.clock_timestamp();
                        SELECT pg_catalog.string_agg(b."Id"::text || ':' || b."Revocaciones"::text, ',' ORDER BY b."Id")
                                   FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > v_ahora)),
                               pg_catalog.count(*)
                                   FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > v_ahora)),
                               pg_catalog.min(b."Id"::text)
                                   FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > v_ahora))::uuid,
                               pg_catalog.min(b."Revocaciones")
                                   FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > v_ahora))
                          INTO v_bloqueadas, v_activas, v_unica, v_revocaciones
                          FROM (SELECT d."Id", d."Revocaciones", d."Activa", d."ExpiraEnUtc"
                                  FROM public."DelegacionesTenant" d
                                 WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                                   AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                                   AND d."Proposito" = 'OperadorExterno'
                                   FOR SHARE) b;

                        IF v_vistas IS DISTINCT FROM v_bloqueadas THEN
                            RAISE EXCEPTION 'Una DelegacionTenant del par cambió mientras se insertaba la Asignación de Operación sin origen (%); reintenta.', NEW."Id"
                                USING ERRCODE = 'serialization_failure';
                        END IF;
                        IF v_activas = 1 THEN
                            NEW."OrigenId" := v_unica;
                            NEW."RevocacionesDelOrigen" := v_revocaciones;
                        END IF;
                        -- Ninguna activa, o varias (ambiguo): sin origen, y la operación no autoriza (los lectores de
                        -- alcance fallan cerrados).
                        RETURN NEW;
                    END IF;
                END IF;

                IF NEW."RevocacionesDelOrigen" IS NULL THEN
                    -- Con origen explícito, el contador es el que el escritor leyó al decidir: sin él no hay con qué
                    -- comprobar que la decisión sigue valiendo.
                    RAISE EXCEPTION 'Una Asignación de Operación con origen trae el contador de revocaciones que leyó (%).', NEW."Id"
                        USING ERRCODE = 'check_violation';
                END IF;

                -- FOR SHARE hasta el final de la transacción: ninguna revocación, reactivación ni ampliación de
                -- caducidad de esta delegación se confirma entre esta lectura y el COMMIT, y si hay una en vuelo se
                -- espera a ella y se lee la fila que confirmó. La caducidad, con el reloj de después del bloqueo.
                SELECT d."Revocaciones", d."Activa", d."ExpiraEnUtc" INTO v_revocaciones, v_activa, v_expira
                  FROM public."DelegacionesTenant" d
                 WHERE d."Id" = NEW."OrigenId"
                   AND d."TenantClienteId" = NEW."PropietarioTenantId"
                   AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                   AND d."Proposito" = 'OperadorExterno'
                   FOR SHARE;
                IF FOUND AND v_activa AND (v_expira IS NULL OR v_expira > pg_catalog.clock_timestamp()) THEN
                    IF v_revocaciones = NEW."RevocacionesDelOrigen" THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'La DelegacionTenant de origen se revocó o se amplió después de decidir la Asignación de Operación (%).', NEW."Id"
                        USING ERRCODE = 'check_violation';
                END IF;
                RAISE EXCEPTION 'La DelegacionTenant de origen no es una delegación OperadorExterno visible, activa y vigente del mismo par (%).', NEW."Id"
                    USING ERRCODE = 'check_violation';
            END;
            $$;
            """;

        /// <summary>
        /// <c>app_asignacion_operacion_sella_origen</c> tal como la deja la fase de expansión
        /// (<c>AnadeOrigenDeOperacionExterna</c>), para el <c>Down</c>.
        /// </summary>
        private const string SellaOrigenExpansion =
            """
            CREATE OR REPLACE FUNCTION public.app_asignacion_operacion_sella_origen() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_revocaciones integer;
                v_activa boolean;
                v_expira public."DelegacionesTenant"."ExpiraEnUtc"%TYPE;
                v_vistas text;
                v_bloqueadas text;
                v_activas bigint;
                v_unica uuid;
            BEGIN
                NEW."TransaccionDeAlta" := pg_catalog.pg_current_xact_id();
                NEW."InicioTransaccionDeAlta" := pg_catalog.now();

                IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" THEN
                    NEW."OrigenId" := NULL;
                    NEW."RevocacionesDelOrigen" := NULL;
                    RETURN NEW;
                END IF;

                IF NEW."OrigenId" IS NULL THEN
                    NEW."RevocacionesDelOrigen" := NULL;
                    IF NEW."Servicio" <> 'Outbound' THEN
                        RETURN NEW;
                    END IF;

                    SELECT pg_catalog.string_agg(d."Id"::text || ':' || d."Revocaciones"::text, ',' ORDER BY d."Id")
                      INTO v_vistas
                      FROM public."DelegacionesTenant" d
                     WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                       AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                       AND d."Proposito" = 'OperadorExterno'
                       AND d."Activa"
                       AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > pg_catalog.now());

                    SELECT pg_catalog.string_agg(b."Id"::text || ':' || b."Revocaciones"::text, ',' ORDER BY b."Id")
                               FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > pg_catalog.now())),
                           pg_catalog.count(*)
                               FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > pg_catalog.now())),
                           pg_catalog.min(b."Id"::text)
                               FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > pg_catalog.now()))::uuid,
                           pg_catalog.min(b."Revocaciones")
                               FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > pg_catalog.now()))
                      INTO v_bloqueadas, v_activas, v_unica, v_revocaciones
                      FROM (SELECT d."Id", d."Revocaciones", d."Activa", d."ExpiraEnUtc"
                              FROM public."DelegacionesTenant" d
                             WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                               AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                               AND d."Proposito" = 'OperadorExterno'
                               FOR SHARE) b;

                    IF v_vistas IS DISTINCT FROM v_bloqueadas THEN
                        RAISE EXCEPTION 'Una DelegacionTenant del par cambió mientras se insertaba la Asignación de Operación sin origen (%); reintenta.', NEW."Id"
                            USING ERRCODE = 'serialization_failure';
                    END IF;
                    IF v_activas = 1 THEN
                        NEW."OrigenId" := v_unica;
                        NEW."RevocacionesDelOrigen" := v_revocaciones;
                    END IF;
                    RETURN NEW;
                END IF;

                IF NEW."RevocacionesDelOrigen" IS NULL THEN
                    RAISE EXCEPTION 'Una Asignación de Operación con origen trae el contador de revocaciones que leyó (%).', NEW."Id"
                        USING ERRCODE = 'check_violation';
                END IF;

                SELECT d."Revocaciones", d."Activa", d."ExpiraEnUtc" INTO v_revocaciones, v_activa, v_expira
                  FROM public."DelegacionesTenant" d
                 WHERE d."Id" = NEW."OrigenId"
                   AND d."TenantClienteId" = NEW."PropietarioTenantId"
                   AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                   AND d."Proposito" = 'OperadorExterno'
                   FOR SHARE;
                IF FOUND AND v_activa AND (v_expira IS NULL OR v_expira > pg_catalog.now()) THEN
                    IF v_revocaciones = NEW."RevocacionesDelOrigen" THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'La DelegacionTenant de origen se revocó o se amplió después de decidir la Asignación de Operación (%).', NEW."Id"
                        USING ERRCODE = 'check_violation';
                END IF;
                RAISE EXCEPTION 'La DelegacionTenant de origen no es una delegación OperadorExterno visible, activa y vigente del mismo par (%).', NEW."Id"
                    USING ERRCODE = 'check_violation';
            END;
            $$;
            """;

        /// <summary>
        /// <c>app_operacion_externa_sella_origen_de_su_transaccion</c> con la caducidad comparada con
        /// <see cref="Reloj"/>: <c>clock_timestamp()</c> en el <c>Up</c>, <c>now()</c> (fase de expansión) en el
        /// <c>Down</c>. La identidad de la transacción de alta sigue siendo <c>now()</c>.
        /// </summary>
        private const string SellaOrigenDeSuTransaccion =
            """
            CREATE OR REPLACE FUNCTION public.app_operacion_externa_sella_origen_de_su_transaccion() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_activas bigint;
            BEGIN
                IF NOT NEW."Activa"
                   OR NEW."Proposito" <> 'OperadorExterno'
                   OR (NEW."ExpiraEnUtc" IS NOT NULL AND NEW."ExpiraEnUtc" <= __RELOJ__) THEN
                    RETURN NULL;
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM public."AsignacionesOperacion" o
                     WHERE o."PropietarioTenantId" = NEW."TenantClienteId"
                       AND o."OperadorTenantId" = NEW."TenantConsultoraId"
                       AND o."PropietarioTenantId" <> o."OperadorTenantId"
                       AND o."Servicio" = 'Outbound'
                       AND o."OrigenId" IS NULL
                       AND o."TransaccionDeAlta" = pg_catalog.pg_current_xact_id()
                       AND o."InicioTransaccionDeAlta" = pg_catalog.now()) THEN
                    RETURN NULL;
                END IF;

                SELECT pg_catalog.count(*)
                           FILTER (WHERE b."Activa" AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > __RELOJ__))
                  INTO v_activas
                  FROM (SELECT d."Activa", d."ExpiraEnUtc"
                          FROM public."DelegacionesTenant" d
                         WHERE d."TenantClienteId" = NEW."TenantClienteId"
                           AND d."TenantConsultoraId" = NEW."TenantConsultoraId"
                           AND d."Proposito" = 'OperadorExterno'
                           FOR SHARE) b;
                IF v_activas <> 1 THEN
                    RETURN NULL;
                END IF;

                BEGIN
                    UPDATE public."AsignacionesOperacion" o
                       SET "OrigenId" = NEW."Id",
                           "RevocacionesDelOrigen" = NEW."Revocaciones"
                     WHERE o."PropietarioTenantId" = NEW."TenantClienteId"
                       AND o."OperadorTenantId" = NEW."TenantConsultoraId"
                       AND o."PropietarioTenantId" <> o."OperadorTenantId"
                       AND o."Servicio" = 'Outbound'
                       AND o."OrigenId" IS NULL
                       AND o."TransaccionDeAlta" = pg_catalog.pg_current_xact_id()
                       AND o."InicioTransaccionDeAlta" = pg_catalog.now();
                EXCEPTION WHEN insufficient_privilege THEN
                    NULL;
                END;
                RETURN NULL;
            END;
            $$;
            """;

        /// <summary>
        /// <c>app_operacion_amparada_por_su_origen(uuid)</c>: el predicado de <c>AmparoDeOrigen</c> sobre una sola
        /// Asignación de Operación, que recibe solo su Id y responde solo un booleano. Lo usa
        /// <c>EncargoAdministracionRepository</c> en todas sus vías (Codex r2 en la PR #2237, media 8): el comando que
        /// registra un Encargo de Administración por Aprovisionamiento escribe como <c>cae_app_aprovisionamiento</c>,
        /// que no tiene ni debe tener <c>SELECT</c> sobre <c>DelegacionesTenant</c>.
        /// <para>
        /// Lee ella misma de la operación el par, el origen y el contador sellado, y compara la caducidad con el reloj de
        /// pared de la base (<c>clock_timestamp()</c>, ronda 4 de Codex, media 2; por eso es <c>VOLATILE</c>): con la
        /// fecha y el contador como argumentos, quien conociera el Id de una delegación y su par podía probar valores
        /// hasta deducir <c>Revocaciones</c> y <c>ExpiraEnUtc</c> (ronda 3 de Codex, media 4).
        /// </para>
        /// <para>
        /// <c>SECURITY DEFINER</c> con el propietario de las tablas y <c>search_path</c> fijo, como
        /// <c>app_sesion_vigente</c>; comprueba que su propietario evita la RLS en vez de responder <c>false</c> a todo.
        /// Solo responde sobre una operación cuyo par vería el contexto firmado (Tenant activo o Tenant de origen en uno
        /// de sus extremos, como las políticas de lectura de <c>DelegacionesTenant</c>), salvo para una sesión cuyo
        /// <c>session_user</c> ya evita la RLS (la misma exención que <c>app_exigir_contexto_firmado</c>); para
        /// cualquier otra, <c>false</c>. <c>EXECUTE</c> solo para los tres roles de la aplicación, que leen operaciones
        /// encargables: <c>cae_app_runtime</c> (Administrador propio), <c>cae_app_soporte</c> (la consulta en una Sesión
        /// Privilegiada) y <c>cae_app_aprovisionamiento</c> (el comando elevado). Decisión provisional de la
        /// coordinadora, pendiente de Chris, frente a conceder <c>SELECT</c> o dejar esa vía sin el filtro.
        /// </para>
        /// </summary>
        public const string CreaFuncionAmparoDeOrigen =
            """
            CREATE FUNCTION public.app_operacion_amparada_por_su_origen(p_operacion uuid)
                RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_tenant uuid := public.app_ctx_tenant_id();
                v_tenant_origen uuid := public.app_ctx_tenant_origen_id();
                v_propietario uuid;
                v_operador uuid;
                v_origen uuid;
                v_revocaciones integer;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación del amparo no vería ninguna DelegacionTenant', current_user;
                END IF;
                SELECT o."PropietarioTenantId", o."OperadorTenantId", o."OrigenId", o."RevocacionesDelOrigen"
                  INTO v_propietario, v_operador, v_origen, v_revocaciones
                  FROM public."AsignacionesOperacion" o
                 WHERE o."Id" = p_operacion
                   AND o."Servicio" = 'Outbound';
                -- Solo Outbound: una DelegacionTenant OperadorExterno solo origina operaciones Outbound. Una de otro
                -- servicio, o una que no existe, no está amparada (falla cerrada).
                IF NOT FOUND THEN
                    RETURN false;
                END IF;
                IF NOT (EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = session_user AND (rolsuper OR rolbypassrls))
                        OR COALESCE(v_tenant IN (v_propietario, v_operador), false)
                        OR COALESCE(v_tenant_origen IN (v_propietario, v_operador), false)) THEN
                    RETURN false;
                END IF;
                -- Una operación interna no depende de ninguna delegación.
                IF v_propietario = v_operador THEN
                    RETURN true;
                END IF;
                RETURN EXISTS (
                    SELECT 1 FROM public."DelegacionesTenant" d
                     WHERE d."Id" = v_origen
                       AND d."TenantClienteId" = v_propietario
                       AND d."TenantConsultoraId" = v_operador
                       AND d."Proposito" = 'OperadorExterno'
                       AND d."Activa"
                       AND (d."ExpiraEnUtc" IS NULL OR pg_catalog.clock_timestamp() < d."ExpiraEnUtc")
                       AND d."Revocaciones" = v_revocaciones);
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_operacion_amparada_por_su_origen(uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.app_operacion_amparada_por_su_origen(uuid)
                TO cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
            """;

        /// <summary>
        /// <c>TR_EncargosAdministracion_ExigeAmparoDeOrigen</c> (ronda 3 de Codex en la PR #2237, media 3): un Encargo de
        /// Administración solo nace sobre una Asignación de Operación externa que su origen ampara en el momento de la
        /// inserción, sea cual sea el escritor. El repositorio ya filtra por el amparo al leer la operación, pero entre esa
        /// lectura y el guardado una revocación o una ampliación de caducidad la dejaban sin amparo.
        /// <para>
        /// Sigue el patrón de los triggers de inserción de esta migración (<c>FOR SHARE</c> sobre la DelegacionTenant de
        /// origen hasta el COMMIT: la revocación espera a este INSERT o este INSERT espera a la revocación y lee la fila
        /// confirmada) y es AFTER, como <c>TR_EncargosAdministracion_OperadorDeLaOperacion</c>, para que una inserción que
        /// la política rechaza siga saliendo con el error de la política. Cuando llega aquí, la clave foránea ya tomó
        /// <c>FOR KEY SHARE</c> sobre la operación; la revocación por Soporte bloquea sus operaciones con
        /// <c>FOR NO KEY UPDATE</c>, compatible con ese bloqueo, así que no se forma el ciclo (ronda 4 de Codex, media 1).
        /// Bloquear en el repositorio no era posible: <c>cae_app_aprovisionamiento</c> no puede leer ni bloquear
        /// <c>DelegacionesTenant</c>, y una función invocable que bloqueara serviría a cualquier rol con <c>EXECUTE</c>
        /// para retener revocaciones. Por eso la función del trigger es <c>SECURITY DEFINER</c>: una función de trigger no
        /// se puede llamar fuera de un trigger, no devuelve nada y solo rechaza; no amplía lo que ningún rol lee ni
        /// escribe. El predicado es el de <c>app_operacion_amparada_por_su_origen</c>, sin una segunda definición, con el
        /// reloj leído después del bloqueo. El rechazo lleva el nombre del trigger como restricción, para que el
        /// repositorio lo distinga.
        /// </para>
        /// </summary>
        public const string CreaExigenciaDeAmparoDelEncargo =
            """
            CREATE FUNCTION public.app_encargo_administracion_exige_amparo_de_origen() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_origen uuid;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación del amparo no vería ninguna DelegacionTenant', current_user;
                END IF;
                -- Un encargo interno no depende de ninguna delegación (y el dominio no lo encarga): que su par sea el de
                -- la operación lo exige TR_EncargosAdministracion_OperadorDeLaOperacion.
                IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" THEN
                    RETURN NULL;
                END IF;
                -- Solo Outbound: una DelegacionTenant OperadorExterno solo origina operaciones Outbound. Sin fila (la
                -- operación no existe, que lo rechaza antes la clave foránea, o es de otro servicio) no hay origen que
                -- bloquear, y app_operacion_amparada_por_su_origen responde false: se rechaza (falla cerrada).
                SELECT o."OrigenId" INTO v_origen
                  FROM public."AsignacionesOperacion" o
                 WHERE o."Id" = NEW."AsignacionOperacionId"
                   AND o."Servicio" = 'Outbound';
                IF v_origen IS NOT NULL THEN
                    PERFORM 1 FROM public."DelegacionesTenant" d WHERE d."Id" = v_origen FOR SHARE;
                END IF;
                IF NOT public.app_operacion_amparada_por_su_origen(NEW."AsignacionOperacionId") THEN
                    RAISE EXCEPTION 'Un Encargo de Administración no nace sobre una Asignación de Operación que su origen ya no ampara (%).', NEW."Id"
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'TR_EncargosAdministracion_ExigeAmparoDeOrigen';
                END IF;
                RETURN NULL;
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_encargo_administracion_exige_amparo_de_origen() FROM PUBLIC;
            CREATE TRIGGER "TR_EncargosAdministracion_ExigeAmparoDeOrigen"
                AFTER INSERT ON public."EncargosAdministracion"
                FOR EACH ROW EXECUTE FUNCTION public.app_encargo_administracion_exige_amparo_de_origen();
            """;

        /// <summary>
        /// <c>app_revocar_operador_cae_externo_por_soporte</c> tal como la deja
        /// <c>SoporteRevocaOperacionesOutboundAcotadas</c>, con el bloqueo de las operaciones en
        /// <see cref="BloqueoDeOperaciones"/>: <c>FOR NO KEY UPDATE</c> en el <c>Up</c> (ronda 4 de Codex, media 1) y
        /// <c>FOR UPDATE</c>, el original, en el <c>Down</c>. <c>CREATE OR REPLACE</c> conserva propietario y permisos.
        /// </summary>
        private const string RevocacionPorSoporte =
            """
            CREATE OR REPLACE FUNCTION public.app_revocar_operador_cae_externo_por_soporte(p_sesion uuid, p_delegacion uuid, p_motivo text) RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
              v_actor uuid;
              v_tenant uuid;
              v_motivo text;
              v_operador uuid;
              v_activa boolean;
              v_expira timestamp with time zone;
              v_motivo_activacion text;
              v_operacion record;
              v_ahora timestamp with time zone := now();
            BEGIN
              IF NOT public.app_ctx_valido() THEN
                RETURN 'contexto_no_valido';
              END IF;
              v_actor := public.app_ctx_usuario_id();
              v_tenant := public.app_ctx_tenant_id();
              IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL OR p_delegacion IS NULL THEN
                RETURN 'contexto_no_valido';
              END IF;

              v_motivo := btrim(p_motivo);
              IF v_motivo IS NULL OR v_motivo = '' OR char_length(v_motivo) > 500 THEN
                RETURN 'motivo_no_valido';
              END IF;

              -- La sesión: abierta, sin simular a nadie, sobre el Tenant del contexto, del
              -- actor del contexto, y amparada por una concesión vigente de esta capacidad,
              -- por Tenant, que alcanza ese Tenant y que concedió otra persona.
              IF NOT EXISTS (
                  SELECT 1
                  FROM public."SesionesPrivilegiadas" s
                  JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
                  WHERE s."Id" = p_sesion
                    AND s."TenantObjetivoId" = v_tenant
                    AND s."UsuarioSimuladoId" IS NULL
                    AND s."CerradaEnUtc" IS NULL
                    AND s."InicioEnUtc" <= v_ahora
                    AND s."ExpiraEnUtc" > v_ahora
                    AND c."UsuarioPlataformaId" = v_actor
                    AND c."Capacidad" = 'RevocacionOperadorCaeExterno'
                    AND c."Estado" = 'Vigente'
                    AND c."EsAlcanceGlobal" = false
                    AND c."VigenciaDesde" <= v_ahora
                    AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora)
                    AND c."ConcedidaPorUsuarioId" IS NOT NULL
                    AND c."ConcedidaPorUsuarioId" <> c."UsuarioPlataformaId"
                    AND EXISTS (
                        SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
                        WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant))
              THEN
                RETURN 'sesion_no_autorizada';
              END IF;

              -- Quien la ejerce es un Actor de Plataforma TALVEG: su cuenta es de un Tenant
              -- de plataforma. EsPlataforma aquí solo restringe, no concede nada.
              IF NOT EXISTS (
                  SELECT 1 FROM public."AspNetUsers" a
                  JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
                  WHERE a."Id" = v_actor AND tp."EsPlataforma")
              THEN
                RETURN 'sesion_no_autorizada';
              END IF;

              -- La Delegación: del Tenant del contexto como Tenant propietario y de un
              -- Operador CAE externo. Inexistente, de otro Tenant o de Soporte, el mismo código.
              SELECT d."TenantConsultoraId", d."Activa", d."ExpiraEnUtc", d."MotivoActivacion"
                INTO v_operador, v_activa, v_expira, v_motivo_activacion
              FROM public."DelegacionesTenant" d
              WHERE d."Id" = p_delegacion
                AND d."TenantClienteId" = v_tenant
                AND d."Proposito" = 'OperadorExterno'
              FOR UPDATE;
              IF NOT FOUND THEN
                RETURN 'delegacion_no_encontrada';
              END IF;
              IF NOT v_activa THEN
                RETURN 'ya_inactiva';
              END IF;

              -- DelegacionTenant.Desactivar.
              UPDATE public."DelegacionesTenant"
              SET "Activa" = false, "ExpiraEnUtc" = NULL, "MotivoActivacion" = NULL
              WHERE "Id" = p_delegacion;

              INSERT INTO public."RegistrosAuditoria"
                ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                 "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
              VALUES
                (uuidv7(), v_tenant, 'DelegacionTenant', p_delegacion, 'Modificado',
                 json_build_object('Activa', true, 'ExpiraEnUtc', v_expira, 'MotivoActivacion', v_motivo_activacion)::text,
                 json_build_object('Activa', false, 'ExpiraEnUtc', NULL, 'MotivoActivacion', NULL,
                                   'OperadorTenantId', v_operador, 'MotivoRevocacionPorSoporte', v_motivo)::text,
                 v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora);

              -- AsignacionesOperativasWriter.CerrarOperacionDelegadaAsync: TODAS las operaciones
              -- externas Outbound no cerradas del par, del Tenant entero y acotadas a un Cliente
              -- empresarial, en cualquier estado no cerrado, y sus carteras no cerradas.
              FOR v_operacion IN
                SELECT o."Id", o."Estado"
                FROM public."AsignacionesOperacion" o
                WHERE NOT o."EsRaiz"
                  AND o."PropietarioTenantId" = v_tenant
                  AND o."OperadorTenantId" = v_operador
                  AND o."Servicio" = 'Outbound'
                  AND o."Estado" <> 'Cerrada'
                ORDER BY o."Id"
                __BLOQUEO_DE_OPERACIONES__
              LOOP
                -- AsignacionCartera.CerrarPorCascadaDeLaOperacion, antes que la operación: cierra
                -- toda cartera no cerrada, en cualquier estado, y apaga la marca de principal
                -- (EraPrincipalAlCerrarsePorCascada queda como histórico: reactivar la delegación
                -- no repone ninguna cartera, decisión de Chris del 2026-11-14).
                WITH previas AS (
                  SELECT c."Id", c."Estado" AS estado_antes, c."EsPrincipal" AS principal_antes
                  FROM public."AsignacionesCartera" c
                  WHERE c."AsignacionOperacionId" = v_operacion."Id"
                    AND c."Estado" <> 'Cerrada'),
                cerradas AS (
                  UPDATE public."AsignacionesCartera" c
                  SET "EraPrincipalAlCerrarsePorCascada" = c."EsPrincipal",
                      "EsPrincipal" = false,
                      "Estado" = 'Cerrada',
                      "MotivoCierre" = 'Revocada',
                      "VigenciaHasta" = CASE
                          WHEN c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > v_ahora
                            THEN GREATEST(v_ahora, c."VigenciaDesde")
                          ELSE c."VigenciaHasta" END,
                      "Version" = uuidv7()
                  FROM previas p
                  WHERE c."Id" = p."Id"
                  RETURNING c."Id", p.estado_antes, p.principal_antes,
                            c."EraPrincipalAlCerrarsePorCascada" AS era, c."VigenciaHasta" AS hasta)
                INSERT INTO public."RegistrosAuditoria"
                  ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                   "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                SELECT uuidv7(), v_tenant, 'AsignacionCartera', x."Id", 'Modificado',
                       json_build_object('Estado', x.estado_antes, 'EsPrincipal', x.principal_antes)::text,
                       json_build_object('Estado', 'Cerrada', 'MotivoCierre', 'Revocada', 'EsPrincipal', false,
                                         'EraPrincipalAlCerrarsePorCascada', x.era,
                                         'VigenciaHasta', x.hasta)::text,
                       v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora
                FROM cerradas x;

                -- AsignacionResponsabilidad.Cerrar sobre la operación.
                UPDATE public."AsignacionesOperacion" o
                SET "Estado" = 'Cerrada',
                    "MotivoCierre" = 'Revocada',
                    "VigenciaHasta" = CASE
                        WHEN o."VigenciaHasta" IS NULL OR o."VigenciaHasta" > v_ahora
                          THEN GREATEST(v_ahora, o."VigenciaDesde")
                        ELSE o."VigenciaHasta" END,
                    "Version" = uuidv7()
                WHERE o."Id" = v_operacion."Id";

                INSERT INTO public."RegistrosAuditoria"
                  ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                   "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                SELECT uuidv7(), v_tenant, 'AsignacionOperacion', o."Id", 'Modificado',
                       json_build_object('Estado', v_operacion."Estado")::text,
                       json_build_object('Estado', 'Cerrada', 'MotivoCierre', 'Revocada', 'VigenciaHasta', o."VigenciaHasta")::text,
                       v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', v_ahora
                FROM public."AsignacionesOperacion" o
                WHERE o."Id" = v_operacion."Id";
              END LOOP;

              RETURN 'revocada';
            END;
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las filas heredadas revocadas por RevocaFilasHeredadasPrevias no se reponen: revocar es definitivo.
            migrationBuilder.Sql(RevocacionPorSoporte.Replace(BloqueoDeOperaciones, "FOR UPDATE"));
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_EncargosAdministracion_ExigeAmparoDeOrigen" ON public."EncargosAdministracion";
                DROP FUNCTION IF EXISTS public.app_encargo_administracion_exige_amparo_de_origen();
                DROP FUNCTION IF EXISTS public.app_operacion_amparada_por_su_origen(uuid);
                DROP TRIGGER IF EXISTS asignacion_operador_delegado_revocacion_definitiva ON public."AsignacionesOperadorDelegado";
                DROP FUNCTION IF EXISTS public.app_asignacion_operador_delegado_revocacion_definitiva();
                DROP TRIGGER IF EXISTS asignacion_operador_delegado_exige_delegacion_activa ON public."AsignacionesOperadorDelegado";
                DROP FUNCTION IF EXISTS public.app_asignacion_operador_delegado_exige_delegacion_activa();
                DROP TRIGGER IF EXISTS delegacion_tenant_revoca_filas_heredadas ON public."DelegacionesTenant";
                DROP FUNCTION IF EXISTS public.app_delegacion_tenant_revoca_filas_heredadas();
                DROP TRIGGER asignacion_operacion_sella_origen ON public."AsignacionesOperacion";
                CREATE TRIGGER asignacion_operacion_sella_origen
                    BEFORE INSERT ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_sella_origen();

                CREATE OR REPLACE FUNCTION public.app_delegacion_tenant_cuenta_revocaciones() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        NEW."Revocaciones" := 0;
                        RETURN NEW;
                    END IF;

                    IF (OLD."Activa" AND NOT NEW."Activa")
                       OR ((OLD."Proposito" = 'OperadorExterno' OR NEW."Proposito" = 'OperadorExterno')
                           AND OLD."Activa" AND NEW."Activa"
                           AND OLD."ExpiraEnUtc" IS NOT NULL
                           AND (NEW."ExpiraEnUtc" IS NULL OR NEW."ExpiraEnUtc" > OLD."ExpiraEnUtc")) THEN
                        NEW."Revocaciones" := OLD."Revocaciones" + 1;
                    ELSE
                        NEW."Revocaciones" := OLD."Revocaciones";
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
            migrationBuilder.Sql(SellaOrigenExpansion);
            migrationBuilder.Sql(SellaOrigenDeSuTransaccion.Replace(Reloj, "pg_catalog.now()"));

            migrationBuilder.DropColumn(
                name: "RevocacionesDelOrigen",
                table: "AsignacionesOperadorDelegado");
        }
    }
}
