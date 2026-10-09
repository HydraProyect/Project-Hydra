using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEncargosAdministracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ViaAcceso",
                table: "RegistrosAuditoria",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "EncargosAdministracion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AsignacionOperacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClausulaContrato = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VersionTexto = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Origen = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RegistradoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenciaDesde = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenciaHasta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetiradoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetiradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncargosAdministracion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EncargosAdministracion_AsignacionesOperacion_AsignacionOper~",
                        columns: x => new { x.AsignacionOperacionId, x.PropietarioTenantId },
                        principalTable: "AsignacionesOperacion",
                        principalColumns: new[] { "Id", "PropietarioTenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncargosAdministracion_AsignacionOperacionId_PropietarioTen~",
                table: "EncargosAdministracion",
                columns: new[] { "AsignacionOperacionId", "PropietarioTenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_EncargosAdministracion_OperadorTenantId",
                table: "EncargosAdministracion",
                column: "OperadorTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EncargosAdministracion_PropietarioTenantId",
                table: "EncargosAdministracion",
                column: "PropietarioTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EncargosAdministracion_VigentePorOperacion",
                table: "EncargosAdministracion",
                column: "AsignacionOperacionId",
                unique: true,
                filter: "\"RetiradoEnUtc\" IS NULL");

            // Catálogo que cruza dos Tenants (CoberturaRlsDelModeloTests, categoría de los catálogos de
            // asignación): RLS SIN FORCE, por el mismo motivo que AsignacionesOperacion y las solicitudes de
            // incorporación — la retirada de un Tenant de demo opera como propietario de la tabla y tiene que
            // ver las filas que borra.
            //
            // posicion_en_el_encargo:
            //   USING       el Tenant propietario lo ve y lo retira; el Operador CAE externo lo lee, por su
            //               Tenant de ORIGEN, para calcular su techo de rol.
            //   WITH CHECK  solo se escribe con el Tenant propietario como Tenant activo, y NUNCA desde una
            //               conexión cuyo Tenant de origen sea el Operador CAE que lo recibe: nadie del
            //               Operador CAE registra ni retira su propio encargo aunque Application fallara.
            //               IS DISTINCT FROM y no <>: sin Tenant de origen (trabajo de fondo) la comparación
            //               con NULL daría NULL y la política negaría; aquí decide la primera mitad.
            //
            // Privilegios: runtime y aprovisionamiento insertan y solo pueden actualizar las dos columnas de
            // la retirada (más el token de concurrencia); ninguno borra (mismo criterio que
            // AuditoriaSoloInsercionParaRuntime). La cláusula, la vigencia, la operación y los dos Tenants
            // no se pueden reescribir ni con un defecto en el código.
            //
            // Los privilegios por columna NO bastan para decir que el registro «solo añade» ni que el
            // Operador CAE de la fila es el de su operación (revisión previa a PR, 2026-10-09): quien puede
            // actualizar la retirada podía también deshacerla o cambiarle el autor, y la clave foránea solo
            // ata (operación, Tenant propietario), no el Operador CAE. Lo cierran dos disparadores, que
            // valen para cualquier rol, también para el propietario de la tabla:
            //
            //   TR_EncargosAdministracion_RetiradaInmutable (BEFORE UPDATE): la retirada se escribe entera
            //     (instante y autor a la vez) y una sola vez; después ni se modifica ni se vuelve a NULL.
            //   TR_EncargosAdministracion_OperadorDeLaOperacion (AFTER INSERT): "OperadorTenantId" tiene que
            //     ser el Operador CAE de la Asignación de Operación referenciada. La función corre con los
            //     permisos de quien inserta (no es SECURITY DEFINER) y lee la operación bajo su RLS: quien
            //     pasa el WITH CHECK tiene el Tenant propietario como Tenant activo, que es justo la
            //     posición desde la que posicion_en_la_asignacion deja ver esa operación. Si no la ve, falla
            //     cerrado. Es AFTER para que una inserción que la política rechaza siga saliendo con el
            //     error de la política y no con el del disparador.
            //
            // cae_app_aprovisionamiento: Soporte TALVEG registra y retira el encargo dentro de una Sesión
            // Privilegiada con la capacidad Aprovisionamiento sobre el Tenant propietario, y ese comando corre
            // con este rol (ElevacionEscrituraAprovisionamientoBehavior). Necesita además leer la operación a
            // la que liga el encargo: SELECT sobre "AsignacionesOperacion", que no amplía lo que ese mismo
            // actor ya lee con cae_app_soporte y sigue acotado por posicion_en_la_asignacion al Tenant objetivo.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."EncargosAdministracion" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY posicion_en_el_encargo ON public."EncargosAdministracion" USING ((("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) OR ("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid))) WITH CHECK ((("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND ("OperadorTenantId" IS DISTINCT FROM (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid)));
                REVOKE ALL ON TABLE public."EncargosAdministracion" FROM cae_app_runtime;
                GRANT SELECT,INSERT ON TABLE public."EncargosAdministracion" TO cae_app_runtime;
                GRANT UPDATE ("RetiradoPorUsuarioId", "RetiradoEnUtc", "Version") ON TABLE public."EncargosAdministracion" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."EncargosAdministracion" TO cae_app_soporte;
                GRANT SELECT,INSERT ON TABLE public."EncargosAdministracion" TO cae_app_aprovisionamiento;
                GRANT UPDATE ("RetiradoPorUsuarioId", "RetiradoEnUtc", "Version") ON TABLE public."EncargosAdministracion" TO cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."AsignacionesOperacion" TO cae_app_aprovisionamiento;

                CREATE FUNCTION public.encargo_administracion_retirada_inmutable() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF (NEW."RetiradoEnUtc" IS NULL) <> (NEW."RetiradoPorUsuarioId" IS NULL) THEN
                        RAISE EXCEPTION 'La retirada del encargo % se escribe entera: instante y autor a la vez.', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF OLD."RetiradoEnUtc" IS NOT NULL
                       AND (NEW."RetiradoEnUtc" IS DISTINCT FROM OLD."RetiradoEnUtc"
                            OR NEW."RetiradoPorUsuarioId" IS DISTINCT FROM OLD."RetiradoPorUsuarioId")
                    THEN
                        RAISE EXCEPTION 'La retirada del encargo % no se modifica ni se deshace.', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION public.encargo_administracion_exige_operador_de_la_operacion() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM public."AsignacionesOperacion" o
                        WHERE o."Id" = NEW."AsignacionOperacionId"
                          AND o."PropietarioTenantId" = NEW."PropietarioTenantId"
                          AND o."OperadorTenantId" = NEW."OperadorTenantId")
                    THEN
                        RAISE EXCEPTION 'El encargo % no nombra al Operador CAE de su Asignación de Operación.', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER "TR_EncargosAdministracion_RetiradaInmutable" BEFORE UPDATE ON public."EncargosAdministracion" FOR EACH ROW EXECUTE FUNCTION public.encargo_administracion_retirada_inmutable();
                CREATE TRIGGER "TR_EncargosAdministracion_OperadorDeLaOperacion" AFTER INSERT ON public."EncargosAdministracion" FOR EACH ROW EXECUTE FUNCTION public.encargo_administracion_exige_operador_de_la_operacion();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE SELECT ON TABLE public."AsignacionesOperacion" FROM cae_app_aprovisionamiento;
                """);

            migrationBuilder.DropTable(
                name: "EncargosAdministracion");

            // Los disparadores caen con la tabla; sus funciones, no.
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.encargo_administracion_exige_operador_de_la_operacion();
                DROP FUNCTION public.encargo_administracion_retirada_inmutable();
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ViaAcceso",
                table: "RegistrosAuditoria",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldNullable: true);
        }
    }
}
