using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeOrigenTextoMensaje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Con DEFAULT en base: mientras el relevo cambia de ranura, la versión anterior sigue insertando mensajes sin
            // conocer la columna, y un mensaje nace siempre como escrito por una persona (0), sin sugerencia.
            migrationBuilder.AddColumn<int>(
                name: "OrigenTexto",
                table: "Mensajes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RespuestaSugeridaId",
                table: "Mensajes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Mensajes_RespuestaSugeridaId_TenantId",
                table: "Mensajes",
                columns: new[] { "RespuestaSugeridaId", "TenantId" },
                filter: "\"RespuestaSugeridaId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Mensajes_OrigenTextoSegunSugerencia",
                table: "Mensajes",
                sql: "(\"OrigenTexto\" = 0 AND \"RespuestaSugeridaId\" IS NULL) OR (\"OrigenTexto\" IN (1, 2) AND \"RespuestaSugeridaId\" IS NOT NULL)");

            // FK compuesta con el Tenant, como las de RespuestasSugeridas hacia Conversaciones y Mensajes: un mensaje solo
            // puede nombrar una sugerencia de su propio Tenant. Hace falta la clave única (Id, TenantId) que la tabla no
            // tenía. Sin ON DELETE y DEFERRABLE INITIALLY DEFERRED: con las FK de RespuestasSugeridas hacia Conversaciones y
            // Mensajes (ambas en cascada) forma un ciclo, y borrar una conversación se lleva mensajes y sugerencias en la
            // misma sentencia; comprobada al confirmar, el orden en que se ejecutan los disparadores de integridad deja de
            // importar. Borrar solo el entrante de origen sigue fallando (23503) al confirmar, porque arrastra la sugerencia
            // que un saliente nombra: la procedencia no se borra en silencio. EF no modela FK diferibles, así que va solo en
            // SQL y el snapshot no cambia. Sin concesiones nuevas: Mensajes ya concede SELECT a cae_app_soporte a nivel de
            // tabla y INSERT a cae_app_runtime, y la RLS de Mensajes no cambia.
            migrationBuilder.Sql("""
                ALTER TABLE public."RespuestasSugeridas"
                    ADD CONSTRAINT "UQ_RespuestasSugeridas_Id_TenantId" UNIQUE ("Id", "TenantId");

                ALTER TABLE public."Mensajes"
                    ADD CONSTRAINT "FK_Mensajes_RespuestasSugeridas_Tenant"
                        FOREIGN KEY ("RespuestaSugeridaId", "TenantId")
                        REFERENCES public."RespuestasSugeridas"("Id", "TenantId")
                        DEFERRABLE INITIALLY DEFERRED;

                -- La procedencia de un texto no se reescribe: el origen y la sugerencia que se guardan al enviar son la prueba
                -- de qué contenido partió de una sugerencia. El disparador corre como quien actualiza y no lee nada.
                CREATE FUNCTION public.impedir_cambio_origen_texto_mensaje() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
                BEGIN
                    RAISE EXCEPTION 'El origen del texto de un mensaje y su sugerencia no cambian' USING ERRCODE = 'check_violation';
                END
                $$;
                REVOKE ALL ON FUNCTION public.impedir_cambio_origen_texto_mensaje() FROM PUBLIC;

                CREATE TRIGGER impedir_cambio_origen_texto_mensaje
                    BEFORE UPDATE OF "OrigenTexto", "RespuestaSugeridaId" ON public."Mensajes"
                    FOR EACH ROW WHEN (OLD."OrigenTexto" IS DISTINCT FROM NEW."OrigenTexto"
                                       OR OLD."RespuestaSugeridaId" IS DISTINCT FROM NEW."RespuestaSugeridaId")
                    EXECUTE FUNCTION public.impedir_cambio_origen_texto_mensaje();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER impedir_cambio_origen_texto_mensaje ON public."Mensajes";
                DROP FUNCTION public.impedir_cambio_origen_texto_mensaje();
                ALTER TABLE public."Mensajes" DROP CONSTRAINT "FK_Mensajes_RespuestasSugeridas_Tenant";
                ALTER TABLE public."RespuestasSugeridas" DROP CONSTRAINT "UQ_RespuestasSugeridas_Id_TenantId";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Mensajes_RespuestaSugeridaId_TenantId",
                table: "Mensajes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Mensajes_OrigenTextoSegunSugerencia",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "OrigenTexto",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "RespuestaSugeridaId",
                table: "Mensajes");
        }
    }
}
