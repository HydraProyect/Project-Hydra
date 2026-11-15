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
    /// UPDATE puede rebajarlo) y toma un cerrojo consultivo exclusivo de la delegación en cada cambio de activación.</item>
    /// <item><c>delegacion_tenant_revoca_filas_heredadas</c>: cada revocación contada de una delegación de Operador CAE
    /// externo revoca sus <c>AsignacionesOperadorDelegado</c> no revocadas. La fila heredada autoriza por sí sola (vía
    /// heredada de <c>TenantsBeneficiariosAutorizados</c>) y, si sobreviviera, volvería a autorizar al reactivar la
    /// delegación (ronda 1 de Codex en la PR #2237, alta 1).</item>
    /// <item><c>asignacion_operacion_sella_origen</c> sella el origen solo en la inserción, desde la fila real de la
    /// delegación y con un cerrojo consultivo compartido de ella hasta el final de la transacción; valida que sea del
    /// mismo par y de propósito OperadorExterno; en una inserción Outbound externa sin origen (la ranura anterior no
    /// conoce la columna) lo deduce solo si el par tiene exactamente una delegación OperadorExterno activa y sin caducar,
    /// y sigue activa tras el cerrojo. En un UPDATE, el origen y su contador son inmutables (tampoco se puede fijar un
    /// origen que no se fijó al nacer) y, con origen, también el par y el servicio; la comprobación va antes que
    /// cualquier otra rama (ronda 1 de Codex en la PR #2237, media 5).</item>
    /// <item><c>asignacion_operacion_origen_activo_al_nacer</c>, diferido al COMMIT: una operación no cerrada no puede
    /// quedar sellada con el contador de una delegación inactiva, porque la reactivación no lo cambia y la volvería a
    /// amparar. Con el cerrojo compartido, una revocación concurrente no puede confirmarse entre el sellado y el COMMIT:
    /// o se confirmó antes (y se sella el contador de la delegación inactiva, que este trigger rechaza) o espera y deja
    /// la operación con un contador viejo (ronda 1 de Codex en la PR #2237, alta 2). Es diferido porque la reactivación
    /// escribe la delegación y la operación nueva en el mismo guardado, en el orden que elija EF.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Lo que existía antes de esta migración no tiene historia de revocaciones: el contador nace a cero para todas, y la
    /// reactivación de una delegación comercial no guarda fecha. Por eso nada anterior puede probar que nació bajo la
    /// activación vigente de su delegación, y falla cerrado (decisión de la coordinadora en la PR #2237, la data actual es
    /// de prueba): la migración revoca las filas heredadas no revocadas de las delegaciones de Operador CAE externo
    /// (<see cref="RevocaFilasHeredadasPrevias"/>), y <c>AsignacionesOperativasBackfillSeeder</c> cierra en cada arranque,
    /// con sus carteras, toda operación Outbound externa no cerrada que siga sin origen (también las que la ranura
    /// anterior inserte sin que el trigger pueda deducirlo). Nunca fija el origen de una operación ya existente.
    /// </para>
    /// <para>
    /// Las funciones no son SECURITY DEFINER: leen y escriben bajo la RLS de quien escribe, que es uno de los dos extremos
    /// del par; la política de <c>AsignacionesOperadorDelegado</c> es la misma condición sobre la misma delegación que la
    /// de <c>DelegacionesTenant</c>. Si la fila de la delegación no es visible, la deducción deja el origen a NULL (falla
    /// cerrada) y fijar un origen explícito se rechaza.
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

                    -- Exclusivo frente al cerrojo compartido con el que se sella una operación de esta delegación.
                    IF OLD."Activa" IS DISTINCT FROM NEW."Activa" OR NEW."Revocaciones" <> OLD."Revocaciones" THEN
                        PERFORM pg_catalog.pg_advisory_xact_lock(
                            pg_catalog.hashtextextended('DelegacionesTenant:' || NEW."Id"::text, 0));
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
                -- reactivar. Con el cerrojo compartido, la revocación espera a este INSERT (y su trigger AFTER la revoca)
                -- o este INSERT ve la revocación confirmada y se rechaza (revisión puente de la ronda 2, PR #2237).
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
                    PERFORM pg_catalog.pg_advisory_xact_lock_shared(
                        pg_catalog.hashtextextended('DelegacionesTenant:' || NEW."DelegacionTenantId"::text, 0));
                    SELECT d."Proposito", d."Activa" INTO v_proposito, v_activa
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."DelegacionTenantId";
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

                CREATE FUNCTION public.app_asignacion_operacion_sella_origen() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_revocaciones integer;
                    v_deducido boolean := false;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        -- Única excepción, y solo desde la comprobación diferida de la propia transacción que insertó
                        -- la fila (pg_trigger_depth y la marca local que ella fija): la deducción al COMMIT de una
                        -- operación que se insertó antes que su DelegacionTenant en el mismo guardado. El contador
                        -- lo sella esa función bajo el cerrojo compartido; no cambia nada más de la fila.
                        IF OLD."OrigenId" IS NULL AND OLD."RevocacionesDelOrigen" IS NULL
                           AND NEW."OrigenId" IS NOT NULL AND NEW."RevocacionesDelOrigen" IS NOT NULL
                           AND pg_catalog.pg_trigger_depth() > 1
                           AND pg_catalog.current_setting('app.deduccion_origen', true) = NEW."Id"::text
                           AND NEW."PropietarioTenantId" = OLD."PropietarioTenantId"
                           AND NEW."OperadorTenantId" = OLD."OperadorTenantId"
                           AND NEW."Servicio" = OLD."Servicio" THEN
                            RETURN NEW;
                        END IF;
                        -- Antes que cualquier otra rama: el origen y su contador solo se fijan al nacer y no cambian
                        -- nunca (tampoco de NULL a un valor: sellar después repondría una operación superviviente), y una
                        -- operación sellada no cambia de par ni de servicio (convertirla en interna borraría el sello).
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

                    IF NEW."OrigenId" IS NULL AND NEW."Servicio" = 'Outbound' THEN
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

                    IF NEW."OrigenId" IS NULL THEN
                        NEW."RevocacionesDelOrigen" := NULL;
                        RETURN NEW;
                    END IF;

                    -- Compartido hasta el final de la transacción: ninguna revocación ni reactivación de esta delegación
                    -- se confirma entre el sellado y el COMMIT. La lectura de abajo es una sentencia nueva y ve lo que
                    -- se confirmó antes de obtener el cerrojo.
                    PERFORM pg_catalog.pg_advisory_xact_lock_shared(
                        pg_catalog.hashtextextended('DelegacionesTenant:' || NEW."OrigenId"::text, 0));

                    SELECT d."Revocaciones" INTO v_revocaciones
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."OrigenId"
                       AND d."TenantClienteId" = NEW."PropietarioTenantId"
                       AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                       AND d."Proposito" = 'OperadorExterno'
                       AND (NOT v_deducido OR d."Activa");
                    IF NOT FOUND THEN
                        IF v_deducido THEN
                            -- Se revocó entre la deducción y el cerrojo: falla cerrada, sin origen.
                            NEW."OrigenId" := NULL;
                            NEW."RevocacionesDelOrigen" := NULL;
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION 'La DelegacionTenant de origen no es una delegación OperadorExterno visible del mismo par (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    NEW."RevocacionesDelOrigen" := v_revocaciones;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operacion_sella_origen() FROM PUBLIC;
                CREATE TRIGGER asignacion_operacion_sella_origen
                    BEFORE INSERT OR UPDATE ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_sella_origen();

                CREATE FUNCTION public.app_asignacion_operacion_origen_activo_al_nacer() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_estado text;
                    v_activa boolean;
                    v_revocaciones integer;
                    v_origen uuid;
                BEGIN
                    IF NEW."OrigenId" IS NULL THEN
                        -- Deducción diferida. EF ordena los INSERT de un mismo guardado por tabla cuando no hay FK que
                        -- los relacione, así que una operación sin origen explícito (la ranura anterior, que no conoce
                        -- la columna) puede insertarse antes que la DelegacionTenant que la ampara y el trigger BEFORE
                        -- no la encuentra. Al COMMIT ya está: se deduce con la misma regla (una sola delegación
                        -- activa y vigente del par) y se sella bajo el cerrojo compartido. Si no es inequívoco, queda
                        -- sin origen y no autoriza.
                        IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" OR NEW."Servicio" <> 'Outbound' THEN
                            RETURN NULL;
                        END IF;
                        SELECT o."Estado", o."OrigenId" INTO v_estado, v_origen
                          FROM public."AsignacionesOperacion" o WHERE o."Id" = NEW."Id";
                        IF NOT FOUND OR v_estado = 'Cerrada' OR v_origen IS NOT NULL THEN
                            RETURN NULL;
                        END IF;
                        SELECT min(d."Id"::text)::uuid INTO v_origen
                          FROM public."DelegacionesTenant" d
                         WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                           AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                           AND d."Proposito" = 'OperadorExterno'
                           AND d."Activa"
                           AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > pg_catalog.now())
                        HAVING count(*) = 1;
                        IF v_origen IS NULL THEN
                            RETURN NULL;
                        END IF;
                        PERFORM pg_catalog.pg_advisory_xact_lock_shared(
                            pg_catalog.hashtextextended('DelegacionesTenant:' || v_origen::text, 0));
                        SELECT d."Revocaciones" INTO v_revocaciones
                          FROM public."DelegacionesTenant" d
                         WHERE d."Id" = v_origen
                           AND d."Activa"
                           AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > pg_catalog.now());
                        IF NOT FOUND THEN
                            RETURN NULL;
                        END IF;
                        PERFORM pg_catalog.set_config('app.deduccion_origen', NEW."Id"::text, true);
                        UPDATE public."AsignacionesOperacion"
                           SET "OrigenId" = v_origen, "RevocacionesDelOrigen" = v_revocaciones
                         WHERE "Id" = NEW."Id";
                        PERFORM pg_catalog.set_config('app.deduccion_origen', '', true);
                        RETURN NULL;
                    END IF;

                    -- El estado de la operación al COMMIT, no el de la inserción: cerrada en la misma transacción no
                    -- autoriza nunca. Si no se ve, se comprueba igual (falla cerrada).
                    SELECT o."Estado" INTO v_estado FROM public."AsignacionesOperacion" o WHERE o."Id" = NEW."Id";
                    IF FOUND AND v_estado = 'Cerrada' THEN
                        RETURN NULL;
                    END IF;

                    SELECT d."Activa", d."Revocaciones" INTO v_activa, v_revocaciones
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."OrigenId";
                    IF FOUND AND NOT v_activa AND v_revocaciones = NEW."RevocacionesDelOrigen" THEN
                        RAISE EXCEPTION 'Una Asignación de Operación externa no nace sellada con una DelegacionTenant inactiva (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operacion_origen_activo_al_nacer() FROM PUBLIC;
                CREATE CONSTRAINT TRIGGER asignacion_operacion_origen_activo_al_nacer
                    AFTER INSERT ON public."AsignacionesOperacion"
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_origen_activo_al_nacer();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las filas heredadas revocadas por RevocaFilasHeredadasPrevias no se reponen: revocar es definitivo.
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS asignacion_operacion_origen_activo_al_nacer ON public."AsignacionesOperacion";
                DROP FUNCTION IF EXISTS public.app_asignacion_operacion_origen_activo_al_nacer();
                DROP TRIGGER IF EXISTS asignacion_operacion_sella_origen ON public."AsignacionesOperacion";
                DROP FUNCTION IF EXISTS public.app_asignacion_operacion_sella_origen();
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
