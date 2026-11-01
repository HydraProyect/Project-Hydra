using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSesionesUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SesionesUsuario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sid = table.Column<Guid>(type: "uuid", nullable: false),
                    EmitidaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SesionesUsuario", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SesionesUsuario_EmitidaEnUtc",
                table: "SesionesUsuario",
                column: "EmitidaEnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SesionesUsuario_Sid",
                table: "SesionesUsuario",
                column: "Sid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SesionesUsuario_TenantId_UsuarioId_RevocadaEnUtc",
                table: "SesionesUsuario",
                columns: new[] { "TenantId", "UsuarioId", "RevocadaEnUtc" });

            // Sesiones de cookie revocables en el servidor (SesionUsuario). Cada fila vive en el Tenant propietario de
            // la cuenta, con la RLS estándar (aislamiento_tenant sobre el contexto firmado, ENABLE + FORCE).
            //
            // Permisos: cae_app_runtime SELECT e INSERT, y UPDATE solo de "RevocadaEnUtc"; sin DELETE (la purga va por
            // función). cae_app_soporte, SELECT, como el resto de tablas del Tenant; cae_app_aprovisionamiento, nada.
            // solo_la_propia_cuenta_revoca (RESTRICTIVE FOR UPDATE): solo la cuenta que firma el contexto revoca sus
            // sesiones, aunque otra cuenta del mismo Tenant pudiera escribir en él. El alta no se acota a la cuenta: llega
            // desde el inicio de sesión, cuando el contexto todavía no tiene usuario. sesion_revocada_no_se_reabre: una
            // revocación no se deshace ni se mueve.
            //
            // app_sesion_vigente(sid, usuario): la pregunta de cada revalidación (cookie y circuito, cada 60 s), que llega
            // antes de que haya Tenant en el contexto. SECURITY DEFINER, solo devuelve un booleano y exige el par
            // (sid, cuenta): el sid es un secreto aleatorio de la cookie. Como app_tenant_de_cuenta, su propietario tiene
            // que evitar la RLS; si no, respondería «revocada» a todo y lo comprueba en vez de callar.
            //
            // Conservación de 5 años (SesionUsuario.AnosConservacion): app_purgar_sesiones_usuario(), con el corte fijo
            // dentro, como app_purgar_inicios_sesion; la pide PurgaIniciosSesionHostedService.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."SesionesUsuario" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."SesionesUsuario" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."SesionesUsuario"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                CREATE POLICY solo_la_propia_cuenta_revoca ON public."SesionesUsuario"
                    AS RESTRICTIVE FOR UPDATE
                    USING ("UsuarioId" = (SELECT public.app_ctx_usuario_id()))
                    WITH CHECK ("UsuarioId" = (SELECT public.app_ctx_usuario_id()));
                REVOKE ALL ON TABLE public."SesionesUsuario" FROM PUBLIC;
                REVOKE ALL ON TABLE public."SesionesUsuario" FROM cae_app_runtime;
                GRANT SELECT, INSERT ON TABLE public."SesionesUsuario" TO cae_app_runtime;
                GRANT UPDATE ("RevocadaEnUtc") ON TABLE public."SesionesUsuario" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."SesionesUsuario" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."SesionesUsuario" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."SesionesUsuario" FROM cae_app_aprovisionamiento;

                CREATE FUNCTION public.app_sesion_usuario_no_se_reabre() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF OLD."RevocadaEnUtc" IS NOT NULL
                       AND NEW."RevocadaEnUtc" IS DISTINCT FROM OLD."RevocadaEnUtc" THEN
                        RAISE EXCEPTION 'Una sesión revocada no se reabre ni cambia su revocación'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_sesion_usuario_no_se_reabre() FROM PUBLIC;
                CREATE TRIGGER sesion_revocada_no_se_reabre
                    BEFORE UPDATE ON public."SesionesUsuario"
                    FOR EACH ROW EXECUTE FUNCTION public.app_sesion_usuario_no_se_reabre();

                CREATE FUNCTION public.app_sesion_vigente(p_sid uuid, p_usuario uuid) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de sesión no vería ninguna fila', current_user;
                    END IF;
                    RETURN EXISTS (SELECT 1 FROM public."SesionesUsuario"
                                    WHERE "Sid" = p_sid AND "UsuarioId" = p_usuario AND "RevocadaEnUtc" IS NULL);
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_sesion_vigente(uuid, uuid) FROM PUBLIC, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_sesion_vigente(uuid, uuid) TO cae_app_runtime, cae_app_soporte;

                CREATE FUNCTION public.app_purgar_sesiones_usuario() RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de sesiones no vería ninguna fila', current_user;
                    END IF;
                    DELETE FROM public."SesionesUsuario"
                     WHERE "EmitidaEnUtc" < pg_catalog.now() - interval '5 years';
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_purgar_sesiones_usuario() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_purgar_sesiones_usuario() TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_purgar_sesiones_usuario();
                DROP FUNCTION public.app_sesion_vigente(uuid, uuid);
                DROP TRIGGER sesion_revocada_no_se_reabre ON public."SesionesUsuario";
                DROP FUNCTION public.app_sesion_usuario_no_se_reabre();
                """);

            migrationBuilder.DropTable(
                name: "SesionesUsuario");
        }
    }
}
