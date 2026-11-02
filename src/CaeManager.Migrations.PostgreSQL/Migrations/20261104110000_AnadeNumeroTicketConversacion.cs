using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Número de ticket: correlativo por Tenant propietario y sin huecos. Lo asigna un disparador porque las
    /// conversaciones nacen por muchas rutas y porque la ranura anterior sigue insertando sin conocer la columna
    /// mientras dura el relevo. El contador vive en la misma transacción que la inserción, así que un rollback
    /// devuelve el número; a cambio, las altas de un mismo Tenant se serializan hasta el commit.
    /// </summary>
    public partial class AnadeNumeroTicketConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sin BYPASSRLS el relleno vería cero filas de cada Tenant y dejaría números a 0.
            migrationBuilder.Sql("""
                DO $migracion$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'AnadeNumeroTicketConversacion necesita un migrador superusuario o con BYPASSRLS';
                    END IF;
                END
                $migracion$;
                """);

            // ALTER TABLE deja la tabla en ACCESS EXCLUSIVE hasta el commit: entre el relleno y la creación del
            // disparador no puede entrar ninguna inserción de la ranura anterior.
            migrationBuilder.AddColumn<long>(
                name: "NumeroTicket",
                table: "Conversaciones",
                type: "bigint",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.Sql("""
                UPDATE public."Conversaciones" AS c
                SET "NumeroTicket" = n.numero
                FROM (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "TenantId" ORDER BY "CreadoEnUtc", "Id") AS numero
                    FROM public."Conversaciones"
                ) AS n
                WHERE c."Id" = n."Id";

                CREATE TABLE public."ContadoresTicketConversacion" (
                    "TenantId" uuid NOT NULL,
                    "UltimoNumero" bigint NOT NULL,
                    CONSTRAINT "PK_ContadoresTicketConversacion" PRIMARY KEY ("TenantId"),
                    CONSTRAINT "CK_ContadoresTicketConversacion_UltimoNumeroPositivo" CHECK ("UltimoNumero" > 0)
                );

                INSERT INTO public."ContadoresTicketConversacion" ("TenantId", "UltimoNumero")
                SELECT "TenantId", MAX("NumeroTicket") FROM public."Conversaciones" GROUP BY "TenantId";

                ALTER TABLE public."ContadoresTicketConversacion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ContadoresTicketConversacion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ContadoresTicketConversacion"
                    USING ("TenantId" = public.app_ctx_tenant_id())
                    WITH CHECK ("TenantId" = public.app_ctx_tenant_id());

                -- Los privilegios por defecto dan de más: el disparador corre como quien inserta, que solo necesita
                -- leer, crear y avanzar el contador. Soporte TALVEG no inserta conversaciones.
                REVOKE ALL ON TABLE public."ContadoresTicketConversacion" FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                REVOKE DELETE, TRUNCATE ON TABLE public."ContadoresTicketConversacion" FROM cae_app_runtime;

                CREATE FUNCTION public.asignar_numero_ticket_conversacion() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
                BEGIN
                    -- La política del contador rechaza un TenantId que no sea el de la sesión.
                    INSERT INTO public."ContadoresTicketConversacion" AS contador ("TenantId", "UltimoNumero")
                    VALUES (NEW."TenantId", 1)
                    ON CONFLICT ("TenantId") DO UPDATE SET "UltimoNumero" = contador."UltimoNumero" + 1
                    RETURNING contador."UltimoNumero" INTO NEW."NumeroTicket";
                    RETURN NEW;
                END
                $$;
                REVOKE ALL ON FUNCTION public.asignar_numero_ticket_conversacion() FROM PUBLIC;

                CREATE TRIGGER asignar_numero_ticket_conversacion
                    BEFORE INSERT ON public."Conversaciones"
                    FOR EACH ROW EXECUTE FUNCTION public.asignar_numero_ticket_conversacion();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Conversaciones_TenantId_NumeroTicket",
                table: "Conversaciones",
                columns: new[] { "TenantId", "NumeroTicket" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversaciones_NumeroTicketPositivo",
                table: "Conversaciones",
                sql: "\"NumeroTicket\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER asignar_numero_ticket_conversacion ON public."Conversaciones";
                DROP FUNCTION public.asignar_numero_ticket_conversacion();
                DROP TABLE public."ContadoresTicketConversacion";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Conversaciones_TenantId_NumeroTicket",
                table: "Conversaciones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversaciones_NumeroTicketPositivo",
                table: "Conversaciones");

            migrationBuilder.DropColumn(
                name: "NumeroTicket",
                table: "Conversaciones");
        }
    }
}
