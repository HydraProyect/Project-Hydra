using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Procedencia de la Asignación de Operación Outbound externa (hallazgos de Codex en las PR #2223 y #2237; opción A
    /// de Chris del 2026-11-14: reactivar una DelegacionTenant nunca repone nada).
    /// <para>
    /// <c>AsignacionesOperacion</c> guarda la DelegacionTenant de la que nace (<c>OrigenId</c>, FK
    /// Restrict) y el número de revocaciones que llevaba esa delegación al nacer (<c>RevocacionesDelOrigen</c>).
    /// <c>DelegacionesTenant.Revocaciones</c> cuenta cuántas veces ha terminado una activación de la delegación: al pasar
    /// de activa a inactiva y, en una de Operador CAE externo, también cuando se alarga o se quita una caducidad mientras
    /// sigue activa (la activación anterior caducó o habría caducado). Los lectores de alcance (<c>AmparoDeOrigen</c>) solo
    /// dan por amparada una operación externa si su delegación de origen sigue activa, sin caducar y con el mismo número
    /// de revocaciones: otra delegación vigente del par no la sostiene, una operación que sobrevivió a una revocación no
    /// vuelve a autorizar al reactivarse, y una operación externa con el origen a NULL falla cerrada.
    /// </para>
    /// <para>
    /// Los triggers hacen que esto no dependa de qué ranura escribe (despliegue azul-verde):
    /// <list type="bullet">
    /// <item><c>delegacion_tenant_cuenta_revocaciones</c> calcula el contador desde la fila anterior (cuenta también las
    /// revocaciones de la ranura anterior, que solo escribe <c>Activa = false</c>, y de la función SQL de Soporte; ningún
    /// UPDATE puede rebajarlo).</item>
    /// <item><c>delegacion_tenant_revoca_filas_heredadas</c>: cada revocación contada de una delegación de Operador CAE
    /// externo revoca sus <c>AsignacionesOperadorDelegado</c> no revocadas. La fila heredada autoriza por sí sola (vía
    /// heredada de <c>TenantsBeneficiariosAutorizados</c>) y, si sobreviviera, volvería a autorizar al reactivar la
    /// delegación (ronda 1 de Codex en la PR #2237, alta 1).</item>
    /// <item><c>asignacion_operador_delegado_exige_delegacion_activa</c>: una fila heredada viva no nace sobre una
    /// delegación de Operador CAE externo inactiva, y <c>asignacion_operador_delegado_revocacion_definitiva</c>: una fila
    /// revocada no se des-revoca y ninguna fila cambia de delegación, de persona ni de rol (ronda 2 de Codex, alta 2).</item>
    /// <item><c>asignacion_operacion_sella_origen</c> comprueba y sella el origen solo en la inserción. Con origen
    /// explícito exige también el contador que el escritor leyó al decidir, y rechaza la inserción si la delegación ya no
    /// es del mismo par y de Operador CAE externo, si está inactiva o caducada, o si su contador actual no es el leído: una
    /// decisión tomada antes de una revocación o de una ampliación de caducidad no se sella con la activación posterior
    /// (ronda 2 de Codex, alta 3). Sin origen (la ranura anterior no conoce la columna) solo lo deduce si el par tiene
    /// exactamente una delegación OperadorExterno activa y sin caducar; si no, queda a NULL y no autoriza. En un UPDATE,
    /// ninguna rama deja cambiar el origen ni el contador (tampoco de NULL a un valor), y con origen tampoco el par ni el
    /// servicio. No hay deducción diferida ni ninguna excepción por marca de sesión o profundidad de trigger: el
    /// escritor guarda la DelegacionTenant antes que la operación (ronda 2 de Codex, alta 1).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Orden de bloqueo único (ronda 2 de Codex, media 7): toda inserción que depende del estado de una delegación toma
    /// primero <c>FOR SHARE</c> sobre su fila de <c>DelegacionesTenant</c>, y toda revocación o reactivación la bloquea en
    /// exclusiva al escribirla (el UPDATE, o el <c>FOR UPDATE</c> previo de la función de Soporte). Es el mismo recurso
    /// en los dos lados y no hay cerrojo consultivo: no puede formarse el ciclo cerrojo consultivo / fila que daba
    /// <c>40P01</c>. La inserción que llega con una revocación en vuelo espera y relee la versión confirmada; la revocación
    /// que llega con una inserción en vuelo espera a su COMMIT y la cuenta (contador nuevo, filas heredadas revocadas).
    /// </para>
    /// <para>
    /// Lo que existía antes de esta migración no tiene historia de revocaciones: el contador nace a cero para todas, y la
    /// reactivación de una delegación comercial no guarda fecha. Por eso nada anterior puede probar que nació bajo la
    /// activación vigente de su delegación, y falla cerrado (decisión de la coordinadora en la PR #2237, la data actual es
    /// de prueba): la migración revoca las filas heredadas no revocadas de las delegaciones de Operador CAE externo
    /// (<see cref="RevocaFilasHeredadasPrevias"/>), y <c>AsignacionesOperativasBackfillSeeder</c> cierra en cada arranque,
    /// con sus carteras, toda operación Outbound externa no cerrada que siga sin origen. Nunca fija el origen de una
    /// operación ya existente ni crea operaciones externas: solo las abre un acto explícito.
    /// </para>
    /// <para>
    /// Las funciones de los triggers no son SECURITY DEFINER: leen y bloquean bajo la RLS de quien escribe, que es uno de
    /// los dos extremos del par; la política de <c>AsignacionesOperadorDelegado</c> es la misma condición sobre la misma
    /// delegación que la de <c>DelegacionesTenant</c>. Si la fila de la delegación no es visible, la deducción deja el
    /// origen a NULL (falla cerrada) y fijar un origen explícito se rechaza. La única SECURITY DEFINER es
    /// <see cref="CreaFuncionAmparoDeOrigen"/>, que solo responde un booleano.
    /// </para>
    /// </summary>
    public partial class AnadeProcedenciaDeOperacionExterna : Migration
    {
        /// <summary>
        /// Revoca, una sola vez, las filas heredadas no revocadas de toda DelegacionTenant de Operador CAE externo: sin
        /// historia de revocaciones previa a esta migración, ninguna puede probar que no sobrevivió a una. Público para
        /// que la suite de integración lo ejerza sobre datos sembrados.
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
                name: "Revocaciones",
                table: "DelegacionesTenant",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "OrigenId",
                table: "AsignacionesOperacion",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevocacionesDelOrigen",
                table: "AsignacionesOperacion",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_OrigenId",
                table: "AsignacionesOperacion",
                column: "OrigenId",
                filter: "\"OrigenId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AsignacionesOperacion_DelegacionesTenant_OrigenId",
                table: "AsignacionesOperacion",
                column: "OrigenId",
                principalTable: "DelegacionesTenant",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(RevocaFilasHeredadasPrevias);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.app_delegacion_tenant_cuenta_revocaciones() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        NEW."Revocaciones" := 0;
                        RETURN NEW;
                    END IF;

                    IF (OLD."Activa" AND NOT NEW."Activa")
                       OR (NEW."Proposito" = 'OperadorExterno' AND OLD."Activa" AND NEW."Activa"
                           AND OLD."ExpiraEnUtc" IS NOT NULL
                           AND (NEW."ExpiraEnUtc" IS NULL OR NEW."ExpiraEnUtc" > OLD."ExpiraEnUtc")) THEN
                        NEW."Revocaciones" := OLD."Revocaciones" + 1;
                    ELSE
                        NEW."Revocaciones" := OLD."Revocaciones";
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_delegacion_tenant_cuenta_revocaciones() FROM PUBLIC;
                CREATE TRIGGER delegacion_tenant_cuenta_revocaciones
                    BEFORE INSERT OR UPDATE ON public."DelegacionesTenant"
                    FOR EACH ROW EXECUTE FUNCTION public.app_delegacion_tenant_cuenta_revocaciones();

                CREATE FUNCTION public.app_delegacion_tenant_revoca_filas_heredadas() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NEW."Proposito" = 'OperadorExterno' AND NEW."Revocaciones" > OLD."Revocaciones" THEN
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
                CREATE FUNCTION public.app_asignacion_operador_delegado_exige_delegacion_activa() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_proposito text;
                    v_activa boolean;
                BEGIN
                    IF NEW."RevocadaEnUtc" IS NOT NULL THEN
                        RETURN NEW;
                    END IF;
                    SELECT d."Proposito", d."Activa" INTO v_proposito, v_activa
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."DelegacionTenantId"
                       FOR SHARE;
                    IF FOUND AND v_proposito = 'OperadorExterno' AND NOT v_activa THEN
                        RAISE EXCEPTION 'Una fila heredada no nace sobre una DelegacionTenant de Operador CAE externo revocada (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
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
                       OR NEW."CreadoEnUtc" IS DISTINCT FROM OLD."CreadoEnUtc" THEN
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

                CREATE FUNCTION public.app_asignacion_operacion_sella_origen() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_revocaciones integer;
                    v_activa boolean;
                    v_expira public."DelegacionesTenant"."ExpiraEnUtc"%TYPE;
                    v_deducido boolean := false;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        -- Sin excepciones: el origen y su contador solo se fijan al nacer y no cambian nunca (tampoco de
                        -- NULL a un valor: sellar después repondría una operación superviviente), y una operación sellada
                        -- no cambia de par ni de servicio (convertirla en interna borraría el sello).
                        IF NEW."OrigenId" IS DISTINCT FROM OLD."OrigenId"
                           OR NEW."RevocacionesDelOrigen" IS DISTINCT FROM OLD."RevocacionesDelOrigen" THEN
                            RAISE EXCEPTION 'La DelegacionTenant de origen de una Asignación de Operación es inmutable (%).', NEW."Id"
                                USING ERRCODE = 'check_violation';
                        END IF;
                        IF OLD."OrigenId" IS NOT NULL
                           AND (NEW."PropietarioTenantId" IS DISTINCT FROM OLD."PropietarioTenantId"
                                OR NEW."OperadorTenantId" IS DISTINCT FROM OLD."OperadorTenantId"
                                OR NEW."Servicio" IS DISTINCT FROM OLD."Servicio") THEN
                            RAISE EXCEPTION 'Una Asignación de Operación con origen no cambia de par ni de servicio (%).', NEW."Id"
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    -- Una Asignación de Operación interna no nace de ninguna DelegacionTenant.
                    IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" THEN
                        NEW."OrigenId" := NULL;
                        NEW."RevocacionesDelOrigen" := NULL;
                        RETURN NEW;
                    END IF;

                    IF NEW."OrigenId" IS NULL THEN
                        IF NEW."Servicio" = 'Outbound' THEN
                            -- Inserción sin origen (la ranura anterior no conoce la columna): se deduce solo si es
                            -- inequívoco; si no, queda a NULL y la operación no autoriza.
                            SELECT min(d."Id"::text)::uuid INTO NEW."OrigenId"
                              FROM public."DelegacionesTenant" d
                             WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                               AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                               AND d."Proposito" = 'OperadorExterno'
                               AND d."Activa"
                               AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > pg_catalog.now())
                            HAVING count(*) = 1;
                            v_deducido := NEW."OrigenId" IS NOT NULL;
                        END IF;
                        IF NOT v_deducido THEN
                            NEW."RevocacionesDelOrigen" := NULL;
                            RETURN NEW;
                        END IF;
                    ELSIF NEW."RevocacionesDelOrigen" IS NULL THEN
                        -- Con origen explícito, el contador es el que el escritor leyó al decidir: sin él no hay con qué
                        -- comprobar que la decisión sigue valiendo.
                        RAISE EXCEPTION 'Una Asignación de Operación con origen trae el contador de revocaciones que leyó (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;

                    -- FOR SHARE hasta el final de la transacción: ninguna revocación, reactivación ni ampliación de
                    -- caducidad de esta delegación se confirma entre esta lectura y el COMMIT, y si hay una en vuelo se
                    -- espera a ella y se lee la fila que confirmó.
                    SELECT d."Revocaciones", d."Activa", d."ExpiraEnUtc" INTO v_revocaciones, v_activa, v_expira
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."OrigenId"
                       AND d."TenantClienteId" = NEW."PropietarioTenantId"
                       AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                       AND d."Proposito" = 'OperadorExterno'
                       FOR SHARE;
                    IF FOUND AND v_activa AND (v_expira IS NULL OR v_expira > pg_catalog.now()) THEN
                        IF v_deducido THEN
                            NEW."RevocacionesDelOrigen" := v_revocaciones;
                            RETURN NEW;
                        END IF;
                        IF v_revocaciones = NEW."RevocacionesDelOrigen" THEN
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION 'La DelegacionTenant de origen se revocó o se amplió después de decidir la Asignación de Operación (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF v_deducido THEN
                        -- Se revocó o caducó entre la deducción y el bloqueo: falla cerrada, sin origen.
                        NEW."OrigenId" := NULL;
                        NEW."RevocacionesDelOrigen" := NULL;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'La DelegacionTenant de origen no es una delegación OperadorExterno visible, activa y vigente del mismo par (%).', NEW."Id"
                        USING ERRCODE = 'check_violation';
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operacion_sella_origen() FROM PUBLIC;
                CREATE TRIGGER asignacion_operacion_sella_origen
                    BEFORE INSERT OR UPDATE ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_sella_origen();
                """);

            migrationBuilder.Sql(CreaFuncionAmparoDeOrigen);
        }

        /// <summary>
        /// <c>app_operacion_amparada_por_su_origen</c>: el predicado de <c>AmparoDeOrigen</c> sobre una sola
        /// Asignación de Operación, con sus columnas como argumentos, y solo un booleano de respuesta. Lo usa
        /// <c>EncargoAdministracionRepository</c> en todas sus vías (Codex r2 en la PR #2237, media 8): el comando que
        /// registra un Encargo de Administración por Aprovisionamiento escribe como <c>cae_app_aprovisionamiento</c>,
        /// que no tiene ni debe tener <c>SELECT</c> sobre <c>DelegacionesTenant</c>.
        /// <para>
        /// <c>SECURITY DEFINER</c> con el propietario de las tablas y <c>search_path</c> fijo, como
        /// <c>app_sesion_vigente</c>; comprueba que su propietario evita la RLS en vez de responder <c>false</c> a todo.
        /// No lee <c>AsignacionesOperacion</c> (las columnas las trae quien ya leyó la operación bajo su RLS) ni devuelve
        /// nada de la DelegacionTenant. Solo responde sobre una delegación que el contexto firmado vería bajo las dos
        /// políticas de lectura de <c>DelegacionesTenant</c> (Tenant activo o Tenant de origen en uno de sus extremos),
        /// salvo para una sesión cuyo <c>session_user</c> ya evita la RLS (la misma exención que
        /// <c>app_exigir_contexto_firmado</c>). <c>EXECUTE</c> solo para los tres roles de la aplicación, que leen
        /// operaciones encargables: <c>cae_app_runtime</c> (Administrador propio), <c>cae_app_soporte</c> (la consulta
        /// en una Sesión Privilegiada) y <c>cae_app_aprovisionamiento</c> (el comando elevado). Decisión provisional
        /// de la coordinadora, pendiente de Chris, frente a conceder <c>SELECT</c> o dejar esa vía sin el filtro.
        /// </para>
        /// </summary>
        public const string CreaFuncionAmparoDeOrigen =
            """
            CREATE FUNCTION public.app_operacion_amparada_por_su_origen(
                    p_propietario uuid, p_operador uuid, p_origen uuid, p_revocaciones_del_origen integer,
                    p_ahora timestamp with time zone)
                RETURNS boolean
                LANGUAGE plpgsql STABLE SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
                v_tenant uuid := public.app_ctx_tenant_id();
                v_tenant_origen uuid := public.app_ctx_tenant_origen_id();
                v_sin_rls boolean;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación del amparo no vería ninguna DelegacionTenant', current_user;
                END IF;
                -- Una operación interna no depende de ninguna delegación.
                IF p_propietario = p_operador THEN
                    RETURN true;
                END IF;
                v_sin_rls := EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                      WHERE rolname = session_user AND (rolsuper OR rolbypassrls));
                RETURN EXISTS (
                    SELECT 1 FROM public."DelegacionesTenant" d
                     WHERE d."Id" = p_origen
                       AND d."TenantClienteId" = p_propietario
                       AND d."TenantConsultoraId" = p_operador
                       AND d."Proposito" = 'OperadorExterno'
                       AND d."Activa"
                       AND (d."ExpiraEnUtc" IS NULL OR p_ahora < d."ExpiraEnUtc")
                       AND d."Revocaciones" = p_revocaciones_del_origen
                       AND (v_sin_rls
                            OR v_tenant IN (d."TenantClienteId", d."TenantConsultoraId")
                            OR v_tenant_origen IN (d."TenantClienteId", d."TenantConsultoraId")));
            END;
            $$;
            REVOKE ALL ON FUNCTION public.app_operacion_amparada_por_su_origen(uuid, uuid, uuid, integer, timestamp with time zone) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.app_operacion_amparada_por_su_origen(uuid, uuid, uuid, integer, timestamp with time zone)
                TO cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las filas heredadas revocadas por RevocaFilasHeredadasPrevias no se reponen: revocar es definitivo.
            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS public.app_operacion_amparada_por_su_origen(uuid, uuid, uuid, integer, timestamp with time zone);
                DROP TRIGGER IF EXISTS asignacion_operacion_sella_origen ON public."AsignacionesOperacion";
                DROP FUNCTION IF EXISTS public.app_asignacion_operacion_sella_origen();
                DROP TRIGGER IF EXISTS asignacion_operador_delegado_revocacion_definitiva ON public."AsignacionesOperadorDelegado";
                DROP FUNCTION IF EXISTS public.app_asignacion_operador_delegado_revocacion_definitiva();
                DROP TRIGGER IF EXISTS asignacion_operador_delegado_exige_delegacion_activa ON public."AsignacionesOperadorDelegado";
                DROP FUNCTION IF EXISTS public.app_asignacion_operador_delegado_exige_delegacion_activa();
                DROP TRIGGER IF EXISTS delegacion_tenant_revoca_filas_heredadas ON public."DelegacionesTenant";
                DROP FUNCTION IF EXISTS public.app_delegacion_tenant_revoca_filas_heredadas();
                DROP TRIGGER IF EXISTS delegacion_tenant_cuenta_revocaciones ON public."DelegacionesTenant";
                DROP FUNCTION IF EXISTS public.app_delegacion_tenant_cuenta_revocaciones();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AsignacionesOperacion_DelegacionesTenant_OrigenId",
                table: "AsignacionesOperacion");

            migrationBuilder.DropIndex(
                name: "IX_AsignacionesOperacion_OrigenId",
                table: "AsignacionesOperacion");

            migrationBuilder.DropColumn(
                name: "Revocaciones",
                table: "DelegacionesTenant");

            migrationBuilder.DropColumn(
                name: "OrigenId",
                table: "AsignacionesOperacion");

            migrationBuilder.DropColumn(
                name: "RevocacionesDelOrigen",
                table: "AsignacionesOperacion");
        }
    }
}
