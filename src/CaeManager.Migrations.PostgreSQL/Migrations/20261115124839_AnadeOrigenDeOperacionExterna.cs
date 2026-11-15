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
    /// Dos triggers hacen que esto no dependa de qué ranura escribe:
    /// <list type="bullet">
    /// <item><c>delegacion_tenant_cuenta_revocaciones</c> calcula el contador desde la fila anterior: cuenta también las
    /// revocaciones de la ranura anterior (que solo escribe <c>Activa = false</c>) y de la función SQL de Soporte, y
    /// ningún UPDATE puede rebajarlo.</item>
    /// <item><c>asignacion_operacion_sella_origen</c> comprueba y sella el origen en la inserción. Con origen explícito
    /// exige el contador que el escritor leyó al decidir y rechaza la inserción si la delegación no es del mismo par y
    /// de Operador CAE externo, si está inactiva o caducada, o si su contador ya no es el leído. Sin origen (la ranura
    /// anterior no conoce la columna) solo lo deduce si el par tiene exactamente una delegación OperadorExterno activa y
    /// sin caducar; si no, queda a NULL.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Lo que NO hace esta migración y queda para la PR #2237: cerrar las operaciones sin origen, revocar las filas
    /// heredadas vivas, la inmutabilidad del origen y del propósito en los UPDATE, la revocación definitiva de las
    /// filas heredadas y la función SECURITY DEFINER del amparo. La migración de la #2237 deja de añadir estas
    /// columnas y estos triggers y pasa a ser posterior a esta.
    /// </para>
    /// <para>
    /// Las funciones no son SECURITY DEFINER: leen y bloquean <c>DelegacionesTenant</c> bajo la RLS de quien escribe
    /// (<c>extremos_de_la_delegacion</c>: el Tenant activo es uno de los dos extremos). Si la fila no es visible, la
    /// deducción deja el origen a NULL y un origen explícito se rechaza. No conceden a ningún rol lectura ni escritura
    /// nueva: el único rol que inserta en <c>AsignacionesOperacion</c>, <c>cae_app_runtime</c>, ya tiene UPDATE sobre
    /// <c>DelegacionesTenant</c>, que es lo que exige el <c>FOR SHARE</c>.
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
                    v_deducido boolean := false;
                BEGIN
                    -- Una Asignación de Operación interna no nace de ninguna DelegacionTenant.
                    IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" THEN
                        NEW."OrigenId" := NULL;
                        NEW."RevocacionesDelOrigen" := NULL;
                        RETURN NEW;
                    END IF;

                    IF NEW."OrigenId" IS NULL THEN
                        IF NEW."Servicio" = 'Outbound' THEN
                            -- Inserción sin origen (la ranura anterior no conoce la columna): se deduce solo si es
                            -- inequívoco; si no, queda a NULL y se lee como antes de esta migración.
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
                        -- Se revocó o caducó entre la deducción y el bloqueo: sin origen, como antes de esta migración.
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
                    BEFORE INSERT ON public."AsignacionesOperacion"
                    FOR EACH ROW EXECUTE FUNCTION public.app_asignacion_operacion_sella_origen();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS asignacion_operacion_sella_origen ON public."AsignacionesOperacion";
                DROP FUNCTION IF EXISTS public.app_asignacion_operacion_sella_origen();
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
