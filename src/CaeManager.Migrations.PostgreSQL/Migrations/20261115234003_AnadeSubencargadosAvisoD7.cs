using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSubencargadosAvisoD7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Subencargados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Finalidad = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UbicacionYTransferencia = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AdmiteExclusionPorTenant = table.Column<bool>(type: "boolean", nullable: false),
                    Conmutador = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Estado = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PropuestoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PropuestoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvisadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FechaMinimaActivacionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActivadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActivadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetiradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoRetirada = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subencargados", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AvisosCambioSubencargado",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubencargadoId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioDestinatarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RegistradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvisadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvisosCambioSubencargado", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AvisosCambioSubencargado_Subencargados_SubencargadoId",
                        column: x => x.SubencargadoId,
                        principalTable: "Subencargados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObjecionesSubencargado",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubencargadoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ObjetadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Efecto = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Conmutador = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjecionesSubencargado", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjecionesSubencargado_Subencargados_SubencargadoId",
                        column: x => x.SubencargadoId,
                        principalTable: "Subencargados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvisosCambioSubencargado_SubencargadoId",
                table: "AvisosCambioSubencargado",
                column: "SubencargadoId");

            migrationBuilder.CreateIndex(
                name: "IX_AvisosCambioSubencargado_TenantId_EnvioId_UsuarioDestinatar~",
                table: "AvisosCambioSubencargado",
                columns: new[] { "TenantId", "EnvioId", "UsuarioDestinatarioId", "Fase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AvisosCambioSubencargado_TenantId_SubencargadoId_Fase_Regis~",
                table: "AvisosCambioSubencargado",
                columns: new[] { "TenantId", "SubencargadoId", "Fase", "RegistradoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ObjecionesSubencargado_SubencargadoId",
                table: "ObjecionesSubencargado",
                column: "SubencargadoId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjecionesSubencargado_TenantId_SubencargadoId",
                table: "ObjecionesSubencargado",
                columns: new[] { "TenantId", "SubencargadoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subencargados_Estado",
                table: "Subencargados",
                column: "Estado");

            // #2010, D-7. Subencargados es catálogo del plano de Plataforma, sin Tenant: lo leen todos (el gate de
            // la IA de cada Tenant consulta si su objeción sigue vigente) y solo lo dan de alta o cambian los
            // Actores de Plataforma TALVEG con AdminPlataforma global vigente (mismo patrón que OrdenMenuLateral).
            // Sin DELETE para nadie de la aplicación: un subencargado se retira, no se borra.
            //
            // Avisos y objeciones son del Tenant propietario: RLS por Tenant y solo SELECT e INSERT (bandeja de
            // salida y constancia de solo inserción; el REVOKE quita lo que ALTER DEFAULT PRIVILEGES concede a
            // toda tabla nueva). Las políticas restrictivas atan cada alta a quien la puede hacer: el aviso, a un
            // Actor de Plataforma TALVEG con concesión global que firma con su propia identidad; la objeción, al
            // Administrador de ese Tenant propietario, también con su propia identidad.
            //
            // cae_app_soporte lee Subencargados y ObjecionesSubencargado, y nada más: es lo que consulta el gate de
            // la IA (InstruccionTratamientoIaService), que también corre dentro de una Sesión Privilegiada de
            // Soporte TALVEG, y es el mismo SELECT que la línea base le concede sobre
            // InstruccionesTratamientoIaTenantPropietario. Sin él, el gate lanzaría 42501 en vez de responder. La
            // RLS de la objeción sigue siendo la del Tenant del contexto. Los avisos no los lee el gate: ni Soporte
            // TALVEG ni cae_app_aprovisionamiento tienen nada sobre ellos, ni aprovisionamiento sobre las otras dos.
            migrationBuilder.Sql("""
                ALTER TABLE public."Subencargados" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."Subencargados" FORCE ROW LEVEL SECURITY;
                CREATE POLICY subencargados_lectura_de_todos ON public."Subencargados"
                    FOR SELECT USING (true);
                CREATE POLICY subencargados_alta_por_admin_plataforma_global ON public."Subencargados"
                    FOR INSERT WITH CHECK ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))));
                CREATE POLICY subencargados_cambio_por_admin_plataforma_global ON public."Subencargados"
                    FOR UPDATE USING ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))))
                    WITH CHECK ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))));
                REVOKE ALL ON TABLE public."Subencargados" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."Subencargados" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."Subencargados" TO cae_app_soporte;

                ALTER TABLE public."AvisosCambioSubencargado" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AvisosCambioSubencargado" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AvisosCambioSubencargado"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                CREATE POLICY solo_el_actor_de_plataforma_avisa ON public."AvisosCambioSubencargado"
                    AS RESTRICTIVE FOR INSERT
                    WITH CHECK (public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))
                                AND "AvisadoPorUsuarioId" = (SELECT public.app_ctx_usuario_id()));
                REVOKE ALL ON TABLE public."AvisosCambioSubencargado" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."AvisosCambioSubencargado" TO cae_app_runtime;

                ALTER TABLE public."ObjecionesSubencargado" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ObjecionesSubencargado" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ObjecionesSubencargado"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                CREATE POLICY solo_el_administrador_objeta ON public."ObjecionesSubencargado"
                    AS RESTRICTIVE FOR INSERT
                    WITH CHECK (public.app_es_administrador_del_tenant((SELECT public.app_ctx_usuario_id()), "TenantId")
                                AND "UsuarioId" = (SELECT public.app_ctx_usuario_id()));
                REVOKE ALL ON TABLE public."ObjecionesSubencargado" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."ObjecionesSubencargado" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."ObjecionesSubencargado" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvisosCambioSubencargado");

            migrationBuilder.DropTable(
                name: "ObjecionesSubencargado");

            migrationBuilder.DropTable(
                name: "Subencargados");
        }
    }
}
