using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeAutorizacionesLecturaDocumental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantContratistaId",
                table: "ContratosAcceso",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutorizacionesLecturaDocumental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantDestinatarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContratoAccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncluyeDocumentosEmpresa = table.Column<bool>(type: "boolean", nullable: false),
                    VigenciaDesde = table.Column<DateOnly>(type: "date", nullable: false),
                    VigenciaHasta = table.Column<DateOnly>(type: "date", nullable: true),
                    ConcedidaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcedidaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevocadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutorizacionesLecturaDocumental", x => x.Id);
                    table.UniqueConstraint("AK_AutorizacionesLecturaDocumental_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_AutorizacionesLectura_DestinatarioAjeno", "\"TenantDestinatarioId\" <> \"TenantId\"");
                    table.CheckConstraint("CK_AutorizacionesLectura_HastaNoAnteriorADesde", "\"VigenciaHasta\" IS NULL OR \"VigenciaHasta\" >= \"VigenciaDesde\"");
                    table.CheckConstraint("CK_AutorizacionesLectura_RevocacionEntera", "(\"RevocadaEnUtc\" IS NULL) = (\"RevocadaPorUsuarioId\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "AutorizacionesLecturaTrabajadores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutorizacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutorizacionesLecturaTrabajadores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutorizacionesLecturaTrabajadores_AutorizacionesLecturaDocu~",
                        columns: x => new { x.TenantId, x.AutorizacionId },
                        principalTable: "AutorizacionesLecturaDocumental",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutorizacionesLecturaTrabajadores_Trabajadores_TenantId_Tra~",
                        columns: x => new { x.TenantId, x.TrabajadorId },
                        principalTable: "Trabajadores",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContratosAcceso_TenantContratistaAjeno",
                table: "ContratosAcceso",
                sql: "\"TenantContratistaId\" IS NULL OR \"TenantContratistaId\" <> \"TenantId\"");

            migrationBuilder.CreateIndex(
                name: "IX_AutorizacionesLectura_UnaSinRevocarPorContrato",
                table: "AutorizacionesLecturaDocumental",
                columns: new[] { "TenantId", "TenantDestinatarioId", "ContratoAccesoId" },
                unique: true,
                filter: "\"RevocadaEnUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutorizacionesLecturaTrabajadores_Trabajador",
                table: "AutorizacionesLecturaTrabajadores",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutorizacionesLecturaTrabajadores_Unico",
                table: "AutorizacionesLecturaTrabajadores",
                columns: new[] { "TenantId", "AutorizacionId", "TrabajadorId" },
                unique: true);

            // "ContratosAcceso"."TenantContratistaId" no necesita GRANT nuevo: el runtime ya tiene UPDATE sobre la tabla
            // entera (AnadeContratosAcceso). Que no sea el propio Tenant lo exige el CHECK de arriba; que se escriba una
            // sola vez, el dominio (ContratoAcceso.VincularTenantContratista) y además la base, con
            // TR_ContratosAcceso_TenantContratistaUnaVez: una vez escrito, cambiarlo o vaciarlo redirigiría en silencio
            // la lectura que el Tenant de la Empresa contratista autorizó, así que solo pasa de NULL a un valor.
            //
            // Las dos tablas nuevas viven en el Tenant de la Empresa contratista: RLS estándar por TenantId, como
            // ContratosAcceso. Ninguna lectura entre Tenants en esta migración: el Tenant destinatario de una
            // autorización no la ve (aislamiento_tenant solo mira app.tenant_id), y no hay función SECURITY DEFINER.
            //
            // Privilegios de cae_app_runtime:
            //   "AutorizacionesLecturaDocumental": SELECT e INSERT, y UPDATE solo de las columnas que escribe una
            //     revocación. Revocar asigna "RevocadaPorUsuarioId" y "RevocadaEnUtc", y ConcurrenciaOptimistaInterceptor
            //     renueva "Version" en toda entidad IVersionable modificada (también cuando cambian sus Trabajadores, que
            //     la renuevan a propósito); EF solo envía en el UPDATE las columnas modificadas. La entidad no hereda de
            //     EntidadBase, así que no hay columnas de baja lógica ni de creación que tocar; la auditoría escribe en
            //     "RegistrosAuditoria", no en esta tabla, y TenantSelladoInterceptor solo sella TenantId en el alta.
            //     Mismo esquema que "EncargosAdministracion". Sin DELETE: una autorización se revoca, no se borra.
            //   "AutorizacionesLecturaTrabajadores": SELECT, INSERT y DELETE; sin UPDATE (asignar es insertar, retirar es
            //     borrar).
            // Soporte TALVEG solo lee las dos. Sin GRANT a cae_app_aprovisionamiento, igual que ContratosAcceso: el
            // aprovisionamiento de un Tenant no concede autorizaciones de lectura. La retirada de un Tenant de demo borra
            // con la identidad de bootstrap (RetiradaTenantDemoService), no con estos roles.
            //
            // Disparadores (plpgsql, sin SECURITY DEFINER, search_path fijado; valen para cualquier rol, también para el
            // propietario de la tabla), con el mismo código check_violation y un mensaje propio, como los de
            // "EncargosAdministracion":
            //   TR_AutorizacionesLectura_Inmutable (BEFORE UPDATE): una fila revocada ya no cambia en nada, y nunca
            //     cambian las columnas de nacimiento (Tenant, destinatario, contrato, alcance, vigencia y quién y cuándo
            //     concedió). Con los privilegios de columna, el runtime ni siquiera llega a intentarlo; el disparador
            //     cubre a quien sí tiene UPDATE completo.
            //   TR_AutorizacionesLecturaTrabajadores_SoloSinRevocar (BEFORE INSERT OR UPDATE OR DELETE): no se asignan,
            //     cambian ni retiran Trabajadores de una autorización revocada; su lista queda como constancia de qué se
            //     autorizó. Lee la autorización con los permisos de quien escribe y bajo su RLS, y la bloquea con FOR SHARE
            //     para que una revocación simultánea espere a que termine la escritura de la fila hija (y al revés). FOR
            //     SHARE pide UPDATE sobre alguna columna, que el runtime tiene. Una sola excepción: el DELETE de una
            //     identidad que no sea cae_app_runtime pasa sin mirar la autorización. Es el borrado de la retirada de un
            //     Tenant de demo (RetiradaTenantDemoService, con la identidad propietaria de bootstrap), que borra las
            //     filas hijas antes que la autorización revocada; ningún otro rol tiene DELETE sobre la tabla (Soporte y
            //     aprovisionamiento no lo tienen), y como la función no es SECURITY DEFINER, current_user es quien borra.
            //     INSERT y UPDATE siguen bloqueados para todos, propietario incluido.
            //   TR_ContratosAcceso_TenantContratistaUnaVez (BEFORE UPDATE OF "TenantContratistaId", con WHEN): una vez
            //     escrito, "TenantContratistaId" no cambia ni se vacía, para ningún rol.
            migrationBuilder.Sql("""
                ALTER TABLE public."AutorizacionesLecturaDocumental" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AutorizacionesLecturaDocumental" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AutorizacionesLecturaDocumental"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."AutorizacionesLecturaDocumental" FROM cae_app_runtime;
                GRANT SELECT,INSERT ON TABLE public."AutorizacionesLecturaDocumental" TO cae_app_runtime;
                GRANT UPDATE ("RevocadaPorUsuarioId", "RevocadaEnUtc", "Version") ON TABLE public."AutorizacionesLecturaDocumental" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."AutorizacionesLecturaDocumental" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."AutorizacionesLecturaDocumental" TO cae_app_soporte;

                ALTER TABLE public."AutorizacionesLecturaTrabajadores" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AutorizacionesLecturaTrabajadores" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AutorizacionesLecturaTrabajadores"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."AutorizacionesLecturaTrabajadores" FROM cae_app_runtime;
                GRANT SELECT,INSERT,DELETE ON TABLE public."AutorizacionesLecturaTrabajadores" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."AutorizacionesLecturaTrabajadores" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."AutorizacionesLecturaTrabajadores" TO cae_app_soporte;

                CREATE FUNCTION public.autorizacion_lectura_inmutable() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF OLD."RevocadaEnUtc" IS NOT NULL THEN
                        RAISE EXCEPTION 'La autorización de lectura % está revocada: la revocación es definitiva y la fila ya no cambia.', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                       OR NEW."TenantDestinatarioId" IS DISTINCT FROM OLD."TenantDestinatarioId"
                       OR NEW."ContratoAccesoId" IS DISTINCT FROM OLD."ContratoAccesoId"
                       OR NEW."IncluyeDocumentosEmpresa" IS DISTINCT FROM OLD."IncluyeDocumentosEmpresa"
                       OR NEW."VigenciaDesde" IS DISTINCT FROM OLD."VigenciaDesde"
                       OR NEW."VigenciaHasta" IS DISTINCT FROM OLD."VigenciaHasta"
                       OR NEW."ConcedidaPorUsuarioId" IS DISTINCT FROM OLD."ConcedidaPorUsuarioId"
                       OR NEW."ConcedidaEnUtc" IS DISTINCT FROM OLD."ConcedidaEnUtc"
                    THEN
                        RAISE EXCEPTION 'La autorización de lectura % no cambia lo que concedió: se revoca y se concede otra.', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION public.autorizacion_lectura_trabajador_solo_sin_revocar() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    revocada timestamp with time zone;
                BEGIN
                    -- La retirada de un Tenant de demo borra con la identidad propietaria: ver el comentario de la migración.
                    IF TG_OP = 'DELETE' AND current_user <> 'cae_app_runtime' THEN
                        RETURN OLD;
                    END IF;
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        SELECT a."RevocadaEnUtc" INTO revocada
                            FROM public."AutorizacionesLecturaDocumental" a
                            WHERE a."TenantId" = OLD."TenantId" AND a."Id" = OLD."AutorizacionId"
                            FOR SHARE;
                        IF revocada IS NOT NULL THEN
                            RAISE EXCEPTION 'La autorización de lectura % está revocada: sus Trabajadores ya no cambian.', OLD."AutorizacionId"
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;
                    SELECT a."RevocadaEnUtc" INTO revocada
                        FROM public."AutorizacionesLecturaDocumental" a
                        WHERE a."TenantId" = NEW."TenantId" AND a."Id" = NEW."AutorizacionId"
                        FOR SHARE;
                    IF revocada IS NOT NULL THEN
                        RAISE EXCEPTION 'La autorización de lectura % está revocada: sus Trabajadores ya no cambian.', NEW."AutorizacionId"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION public.contrato_acceso_tenant_contratista_una_vez() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    RAISE EXCEPTION 'El contrato de acceso % ya está vinculado con el Tenant de la Empresa contratista: el vínculo no cambia ni se vacía.', OLD."Id"
                        USING ERRCODE = 'check_violation';
                END;
                $$;

                CREATE TRIGGER "TR_AutorizacionesLectura_Inmutable" BEFORE UPDATE ON public."AutorizacionesLecturaDocumental" FOR EACH ROW EXECUTE FUNCTION public.autorizacion_lectura_inmutable();
                CREATE TRIGGER "TR_AutorizacionesLecturaTrabajadores_SoloSinRevocar" BEFORE INSERT OR UPDATE OR DELETE ON public."AutorizacionesLecturaTrabajadores" FOR EACH ROW EXECUTE FUNCTION public.autorizacion_lectura_trabajador_solo_sin_revocar();
                CREATE TRIGGER "TR_ContratosAcceso_TenantContratistaUnaVez" BEFORE UPDATE OF "TenantContratistaId" ON public."ContratosAcceso"
                    FOR EACH ROW
                    WHEN (OLD."TenantContratistaId" IS NOT NULL AND NEW."TenantContratistaId" IS DISTINCT FROM OLD."TenantContratistaId")
                    EXECUTE FUNCTION public.contrato_acceso_tenant_contratista_una_vez();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutorizacionesLecturaTrabajadores");

            migrationBuilder.DropTable(
                name: "AutorizacionesLecturaDocumental");

            // Los disparadores de las tablas nuevas caen con ellas; sus funciones, no. El de ContratosAcceso se quita a mano
            // antes que la columna que vigila.
            migrationBuilder.Sql(
                """
                DROP TRIGGER "TR_ContratosAcceso_TenantContratistaUnaVez" ON public."ContratosAcceso";
                DROP FUNCTION public.contrato_acceso_tenant_contratista_una_vez();
                DROP FUNCTION public.autorizacion_lectura_trabajador_solo_sin_revocar();
                DROP FUNCTION public.autorizacion_lectura_inmutable();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContratosAcceso_TenantContratistaAjeno",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "TenantContratistaId",
                table: "ContratosAcceso");
        }
    }
}
