using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Fase de expansión de la procedencia de la Asignación de Operación Outbound externa (decisión de Chris del
    /// 2026-11-15 sobre la PR #2237, opción c: corte en dos PR). Compatible con la ranura anterior del despliegue
    /// azul-verde, que no conoce ninguna de estas columnas.
    /// <para>
    /// <c>AsignacionesOperacion</c> guarda la DelegacionTenant de la que nace (<c>OrigenId</c>, FK Restrict) y el número
    /// de revocaciones que llevaba esa delegación al nacer (<c>RevocacionesDelOrigen</c>).
    /// <c>DelegacionesTenant.Revocaciones</c> cuenta cuántas veces ha terminado una activación de la delegación: al pasar
    /// de activa a inactiva y, en una de Operador CAE externo, también cuando se alarga o se quita una caducidad mientras
    /// sigue activa. Los lectores de alcance (<c>AmparoDeOrigen.SinOrigenOAmparadasPorSuOrigen</c>) solo dan por amparada
    /// una operación externa CON origen si esa delegación sigue activa, sin caducar y con el mismo número de
    /// revocaciones; una operación SIN origen se sigue leyendo como antes de esta migración.
    /// </para>
    /// <para>
    /// Los triggers hacen que esto no dependa de qué ranura escribe:
    /// <list type="bullet">
    /// <item><c>delegacion_tenant_cuenta_revocaciones</c> calcula el contador desde la fila anterior: cuenta también las
    /// revocaciones de la ranura anterior (que solo escribe <c>Activa = false</c>) y de la función SQL de Soporte, y
    /// ningún UPDATE puede rebajarlo.</item>
    /// <item><c>asignacion_operacion_sella_origen</c> comprueba y sella el origen en la inserción. Con origen explícito
    /// exige el contador que el escritor leyó al decidir y rechaza la inserción si la delegación no es del mismo par y
    /// de Operador CAE externo, si está inactiva o caducada, o si su contador ya no es el leído. Sin origen (la ranura
    /// anterior no conoce la columna) bloquea con <c>FOR SHARE</c> todas las delegaciones OperadorExterno del par y
    /// decide sobre las filas ya bloqueadas: lo deduce si hay exactamente una activa y sin caducar, y si no, lo deja a
    /// NULL. Si lo que vio la sentencia antes de bloquear no es lo que encuentra bloqueado (una revocación, una
    /// reactivación o una ampliación se confirmó entre medias), rechaza la inserción con <c>serialization_failure</c>
    /// (40001, reintentable): dejarla sin origen la sacaría de la revocación que se estaba confirmando (ronda 1 de Codex
    /// en la PR #2253, alta 1).</item>
    /// <item><c>operacion_externa_sella_origen_de_su_transaccion</c>, AFTER INSERT OR UPDATE en
    /// <c>DelegacionesTenant</c>, cubre el orden de guardado de la ranura anterior: EF no garantiza insertar la
    /// delegación antes que la operación (su modelo no conoce la FK) ni guardar la reactivación antes del INSERT de la
    /// operación nueva. Cuando la delegación queda activa, sin caducar y como la única OperadorExterno así del par
    /// (el mismo predicado que la deducción), sella con ella las operaciones Outbound externas del par sin origen que
    /// nacieron en ESTA transacción (ronda 1 de Codex en la PR #2253, alta 3). Ninguna operación anterior se sella por
    /// esta vía.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>La transacción de alta de la operación.</b> <c>TransaccionDeAlta</c> (<c>xid8</c>) e
    /// <c>InicioTransaccionDeAlta</c> guardan el <c>pg_current_xact_id()</c> y el <c>now()</c> de la transacción que
    /// inserta la operación, con el mismo patrón y por los mismos motivos que <c>AlcanceDeConcesionSoloEnSuCreacion</c>:
    /// <c>xmin</c> no sirve porque dentro de un punto de guardado (EF abre uno en cada <c>SaveChanges</c> dentro de la
    /// transacción de <c>TransaccionDeComando</c>) lleva el identificador de la subtransacción y además da la vuelta; el
    /// inicio cubre una copia restaurada en otro clúster. Los sella <c>asignacion_operacion_sella_origen</c> en cada
    /// inserción y <c>asignacion_operacion_conserva_su_alta</c> los conserva en cada UPDATE. No entran en el modelo de EF
    /// Core: ninguna ranura los lee ni los escribe, y las operaciones anteriores quedan a NULL, que nunca coincide.
    /// </para>
    /// <para>
    /// Lo que NO hace esta migración y queda para la PR #2237: cerrar las operaciones sin origen, revocar las filas
    /// heredadas vivas, la inmutabilidad del origen y del propósito en los UPDATE, la revocación definitiva de las
    /// filas heredadas y la función SECURITY DEFINER del amparo. La migración de la #2237 deja de añadir estas
    /// columnas y estos triggers y pasa a ser posterior a esta.
    /// </para>
    /// <para>
    /// Las funciones no son SECURITY DEFINER: leen, bloquean y sellan bajo la RLS de quien escribe
    /// (<c>extremos_de_la_delegacion</c> en <c>DelegacionesTenant</c>, <c>posicion_en_la_asignacion</c> en
    /// <c>AsignacionesOperacion</c>). Si la fila no es visible, la deducción deja el origen a NULL, un origen explícito se
    /// rechaza y el sellado de la transacción no toca la operación. No conceden a ningún rol lectura ni escritura nueva:
    /// el único rol que inserta en <c>AsignacionesOperacion</c>, <c>cae_app_runtime</c>, ya tiene UPDATE sobre
    /// <c>DelegacionesTenant</c> (lo que exige el <c>FOR SHARE</c>) y sobre <c>AsignacionesOperacion</c> (lo que exige el
    /// sellado). Sellar nunca amplía nada: una operación sin origen la autoriza cualquier delegación activa del par, y
    /// sellada solo la suya, sin revocaciones desde entonces.
    /// </para>
    /// </summary>
    public partial class AnadeOrigenDeOperacionExterna : Migration
    {
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

            migrationBuilder.Sql(
                """
                ALTER TABLE public."AsignacionesOperacion"
                    ADD COLUMN "TransaccionDeAlta" xid8,
                    ADD COLUMN "InicioTransaccionDeAlta" timestamp with time zone;

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
                REVOKE ALL ON FUNCTION public.app_delegacion_tenant_cuenta_revocaciones() FROM PUBLIC;
                CREATE TRIGGER delegacion_tenant_cuenta_revocaciones
                    BEFORE INSERT OR UPDATE ON public."DelegacionesTenant"
                    FOR EACH ROW EXECUTE FUNCTION public.app_delegacion_tenant_cuenta_revocaciones();

                CREATE FUNCTION public.app_asignacion_operacion_sella_origen() RETURNS trigger
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

                        -- Inserción sin origen (la ranura anterior no conoce la columna). Primero, las candidatas que ve
                        -- esta sentencia sin bloquear nada.
                        SELECT pg_catalog.string_agg(d."Id"::text || ':' || d."Revocaciones"::text, ',' ORDER BY d."Id")
                          INTO v_vistas
                          FROM public."DelegacionesTenant" d
                         WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                           AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                           AND d."Proposito" = 'OperadorExterno'
                           AND d."Activa"
                           AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > pg_catalog.now());

                        -- Después, FOR SHARE sobre TODAS las OperadorExterno del par, activas o no, hasta el final de la
                        -- transacción: se espera a la revocación, reactivación o ampliación que esté en vuelo y se decide
                        -- sobre las filas que confirmó, ya bloqueadas. Sin la condición de Activa en el WHERE del
                        -- bloqueo: con ella, una candidata revocada mientras se esperaba saldría del resultado en
                        -- silencio y la operación nacería sin origen.
                        SELECT pg_catalog.string_agg(b."Id"::text || ':' || b."Revocaciones"::text, ',' ORDER BY b."Id"),
                               pg_catalog.count(*),
                               pg_catalog.min(b."Id"::text)::uuid,
                               pg_catalog.min(b."Revocaciones")
                          INTO v_bloqueadas, v_activas, v_unica, v_revocaciones
                          FROM (SELECT d."Id", d."Revocaciones", d."Activa", d."ExpiraEnUtc"
                                  FROM public."DelegacionesTenant" d
                                 WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                                   AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                                   AND d."Proposito" = 'OperadorExterno'
                                   FOR SHARE) b
                         WHERE b."Activa"
                           AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > pg_catalog.now());

                        IF v_vistas IS DISTINCT FROM v_bloqueadas THEN
                            RAISE EXCEPTION 'Una DelegacionTenant del par cambió mientras se insertaba la Asignación de Operación sin origen (%); reintenta.', NEW."Id"
                                USING ERRCODE = 'serialization_failure';
                        END IF;
                        IF v_activas = 1 THEN
                            NEW."OrigenId" := v_unica;
                            NEW."RevocacionesDelOrigen" := v_revocaciones;
                        END IF;
                        -- Ninguna activa, o varias (ambiguo): sin origen, como antes de esta migración.
                        RETURN NEW;
                    END IF;

                    IF NEW."RevocacionesDelOrigen" IS NULL THEN
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
                REVOKE ALL ON FUNCTION public.app_asignacion_operacion_sella_origen() FROM PUBLIC;
                CREATE TRIGGER asignacion_operacion_sella_origen
                    BEFORE INSERT ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_sella_origen();

                -- La transacción de alta no cambia en un UPDATE: si se pudiera reescribir, una operación anterior
                -- pasaría por nacida en esta transacción y la sellaría operacion_externa_sella_origen_de_su_transaccion.
                CREATE FUNCTION public.app_asignacion_operacion_conserva_su_alta() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    NEW."TransaccionDeAlta" := OLD."TransaccionDeAlta";
                    NEW."InicioTransaccionDeAlta" := OLD."InicioTransaccionDeAlta";
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operacion_conserva_su_alta() FROM PUBLIC;
                CREATE TRIGGER asignacion_operacion_conserva_su_alta
                    BEFORE UPDATE ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_conserva_su_alta();

                -- El orden de guardado de la ranura anterior: la operación puede insertarse antes que la delegación que
                -- la ampara, o antes del UPDATE que la reactiva, en la misma transacción. Cuando la delegación queda
                -- activa, sin caducar y como la única OperadorExterno así del par, sella las operaciones Outbound
                -- externas del par sin origen nacidas en esta transacción. El caso normal (ninguna) no bloquea nada.
                CREATE FUNCTION public.app_operacion_externa_sella_origen_de_su_transaccion() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_activas bigint;
                BEGIN
                    IF NOT NEW."Activa"
                       OR NEW."Proposito" <> 'OperadorExterno'
                       OR (NEW."ExpiraEnUtc" IS NOT NULL AND NEW."ExpiraEnUtc" <= pg_catalog.now()) THEN
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

                    -- Mismo predicado de unicidad que la deducción, sobre las OperadorExterno del par bloqueadas.
                    SELECT pg_catalog.count(*) INTO v_activas
                      FROM (SELECT d."Activa", d."ExpiraEnUtc"
                              FROM public."DelegacionesTenant" d
                             WHERE d."TenantClienteId" = NEW."TenantClienteId"
                               AND d."TenantConsultoraId" = NEW."TenantConsultoraId"
                               AND d."Proposito" = 'OperadorExterno'
                               FOR SHARE) b
                     WHERE b."Activa"
                       AND (b."ExpiraEnUtc" IS NULL OR b."ExpiraEnUtc" > pg_catalog.now());
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
                        -- La RLS de quien escribe la delegación no le deja reescribir la operación: queda sin origen,
                        -- como antes de esta migración, y la escritura de la delegación sigue adelante.
                        NULL;
                    END;
                    RETURN NULL;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_operacion_externa_sella_origen_de_su_transaccion() FROM PUBLIC;
                CREATE TRIGGER operacion_externa_sella_origen_de_su_transaccion
                    AFTER INSERT OR UPDATE ON public."DelegacionesTenant"
                    FOR EACH ROW EXECUTE FUNCTION public.app_operacion_externa_sella_origen_de_su_transaccion();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS operacion_externa_sella_origen_de_su_transaccion ON public."DelegacionesTenant";
                DROP FUNCTION IF EXISTS public.app_operacion_externa_sella_origen_de_su_transaccion();
                DROP TRIGGER IF EXISTS asignacion_operacion_conserva_su_alta ON public."AsignacionesOperacion";
                DROP FUNCTION IF EXISTS public.app_asignacion_operacion_conserva_su_alta();
                DROP TRIGGER IF EXISTS asignacion_operacion_sella_origen ON public."AsignacionesOperacion";
                DROP FUNCTION IF EXISTS public.app_asignacion_operacion_sella_origen();
                DROP TRIGGER IF EXISTS delegacion_tenant_cuenta_revocaciones ON public."DelegacionesTenant";
                DROP FUNCTION IF EXISTS public.app_delegacion_tenant_cuenta_revocaciones();
                ALTER TABLE public."AsignacionesOperacion"
                    DROP COLUMN IF EXISTS "TransaccionDeAlta",
                    DROP COLUMN IF EXISTS "InicioTransaccionDeAlta";
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
