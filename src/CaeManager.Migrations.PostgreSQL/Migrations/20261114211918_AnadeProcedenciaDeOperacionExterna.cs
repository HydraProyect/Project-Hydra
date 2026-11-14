using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Procedencia de la Asignación de Operación Outbound externa (hallazgos de Codex en la PR #2223; opción A de
    /// Chris del 2026-11-14: reactivar una DelegacionTenant nunca repone nada).
    /// <para>
    /// <c>AsignacionesOperacion</c> guarda la DelegacionTenant de la que nace (<c>DelegacionTenantOrigenId</c>, FK
    /// Restrict) y el número de revocaciones que llevaba esa delegación al nacer (<c>RevocacionesDelegacionOrigen</c>).
    /// <c>DelegacionesTenant.Revocaciones</c> cuenta cuántas veces ha pasado de activa a inactiva. Los lectores de
    /// alcance (<c>AmparoDeDelegacionDeOrigen</c>) solo dan por amparada una operación externa si su delegación de
    /// origen sigue activa, sin caducar y con el mismo número de revocaciones: otra delegación vigente del par no la
    /// sostiene, una operación que sobrevivió a una revocación no vuelve a autorizar al reactivarse, y una operación
    /// externa con el origen a NULL falla cerrada.
    /// </para>
    /// <para>
    /// Dos triggers hacen que esto no dependa de qué ranura escribe (despliegue azul-verde):
    /// <c>delegacion_tenant_cuenta_revocaciones</c> calcula el contador desde la fila anterior, así que cuenta también
    /// las revocaciones de la ranura anterior (que solo escribe <c>Activa = false</c>) y de la función SQL de Soporte, y
    /// ningún UPDATE puede rebajarlo; <c>asignacion_operacion_sella_origen</c> sella el contador desde la fila real de la
    /// delegación al fijar el origen, valida que la delegación sea del mismo par y de propósito OperadorExterno, hace el
    /// origen inmutable una vez fijado y, en una inserción Outbound externa sin origen (la ranura anterior no conoce la
    /// columna), lo deduce solo si el par tiene exactamente una delegación OperadorExterno activa y sin caducar.
    /// </para>
    /// <para>
    /// La migración no rellena el origen de las operaciones existentes: lo hace <c>AsignacionesOperativasBackfillSeeder</c>
    /// en cada arranque, porque así repara también las que la ranura anterior cree sin origen después de aplicar la
    /// migración, y porque cerrar las ambiguas exige el <c>Cerrar</c> del dominio (con sus carteras en cascada). Entre la
    /// migración y el seeder, una operación externa sin origen no autoriza en la ranura nueva: falla cerrada.
    /// </para>
    /// <para>
    /// Las funciones no son SECURITY DEFINER: leen <c>DelegacionesTenant</c> bajo la RLS de quien escribe, que es uno de
    /// los dos extremos del par. Si la fila no es visible, la deducción deja el origen a NULL (falla cerrada) y fijar un
    /// origen explícito se rechaza.
    /// </para>
    /// </summary>
    public partial class AnadeProcedenciaDeOperacionExterna : Migration
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
                name: "DelegacionTenantOrigenId",
                table: "AsignacionesOperacion",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevocacionesDelegacionOrigen",
                table: "AsignacionesOperacion",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_DelegacionTenantOrigenId",
                table: "AsignacionesOperacion",
                column: "DelegacionTenantOrigenId",
                filter: "\"DelegacionTenantOrigenId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AsignacionesOperacion_DelegacionesTenant_DelegacionTenantOr~",
                table: "AsignacionesOperacion",
                column: "DelegacionTenantOrigenId",
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
                    ELSIF OLD."Activa" AND NOT NEW."Activa" THEN
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
                BEGIN
                    -- Una Asignación de Operación interna no nace de ninguna DelegacionTenant.
                    IF NEW."PropietarioTenantId" = NEW."OperadorTenantId" THEN
                        NEW."DelegacionTenantOrigenId" := NULL;
                        NEW."RevocacionesDelegacionOrigen" := NULL;
                        RETURN NEW;
                    END IF;

                    IF TG_OP = 'UPDATE' THEN
                        -- Fijado el origen, ni el origen ni su contador cambian: cerrar o expirar no lo tocan, y
                        -- nada puede volver a sellar el contador de una operación que sobrevivió a una revocación.
                        IF OLD."DelegacionTenantOrigenId" IS NOT NULL THEN
                            IF NEW."DelegacionTenantOrigenId" IS DISTINCT FROM OLD."DelegacionTenantOrigenId"
                               OR NEW."RevocacionesDelegacionOrigen" IS DISTINCT FROM OLD."RevocacionesDelegacionOrigen" THEN
                                RAISE EXCEPTION 'La DelegacionTenant de origen de una Asignación de Operación es inmutable (%).', NEW."Id"
                                    USING ERRCODE = 'check_violation';
                            END IF;
                            RETURN NEW;
                        END IF;
                    ELSIF NEW."DelegacionTenantOrigenId" IS NULL AND NEW."Servicio" = 'Outbound' THEN
                        -- Inserción sin origen (la ranura anterior no conoce la columna): se deduce solo si es
                        -- inequívoco; si no, queda a NULL y la operación no autoriza.
                        SELECT min(d."Id"::text)::uuid INTO NEW."DelegacionTenantOrigenId"
                          FROM public."DelegacionesTenant" d
                         WHERE d."TenantClienteId" = NEW."PropietarioTenantId"
                           AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                           AND d."Proposito" = 'OperadorExterno'
                           AND d."Activa"
                           AND (d."ExpiraEnUtc" IS NULL OR d."ExpiraEnUtc" > pg_catalog.now())
                        HAVING count(*) = 1;
                    END IF;

                    IF NEW."DelegacionTenantOrigenId" IS NULL THEN
                        NEW."RevocacionesDelegacionOrigen" := NULL;
                        RETURN NEW;
                    END IF;

                    SELECT d."Revocaciones" INTO v_revocaciones
                      FROM public."DelegacionesTenant" d
                     WHERE d."Id" = NEW."DelegacionTenantOrigenId"
                       AND d."TenantClienteId" = NEW."PropietarioTenantId"
                       AND d."TenantConsultoraId" = NEW."OperadorTenantId"
                       AND d."Proposito" = 'OperadorExterno';
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'La DelegacionTenant de origen no es una delegación OperadorExterno visible del mismo par (%).', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    NEW."RevocacionesDelegacionOrigen" := v_revocaciones;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_asignacion_operacion_sella_origen() FROM PUBLIC;
                CREATE TRIGGER asignacion_operacion_sella_origen
                    BEFORE INSERT OR UPDATE ON public."AsignacionesOperacion"
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
                name: "FK_AsignacionesOperacion_DelegacionesTenant_DelegacionTenantOr~",
                table: "AsignacionesOperacion");

            migrationBuilder.DropIndex(
                name: "IX_AsignacionesOperacion_DelegacionTenantOrigenId",
                table: "AsignacionesOperacion");

            migrationBuilder.DropColumn(
                name: "Revocaciones",
                table: "DelegacionesTenant");

            migrationBuilder.DropColumn(
                name: "DelegacionTenantOrigenId",
                table: "AsignacionesOperacion");

            migrationBuilder.DropColumn(
                name: "RevocacionesDelegacionOrigen",
                table: "AsignacionesOperacion");
        }
    }
}
