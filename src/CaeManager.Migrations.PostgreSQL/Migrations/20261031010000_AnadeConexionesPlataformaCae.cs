using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeConexionesPlataformaCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AddOnSubidaAutomaticaActivo",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ConectorApiActivo",
                table: "ProveedoresPlataformaCae",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ConexionesPlataformaCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProveedorPlataformaCaeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConexionesPlataformaCae", x => x.Id);
                    table.UniqueConstraint("AK_ConexionesPlataformaCae_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ConexionesPlataformaCae_ProveedoresPlataformaCae_ProveedorP~",
                        column: x => x.ProveedorPlataformaCaeId,
                        principalTable: "ProveedoresPlataformaCae",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CredencialesConexionPlataformaCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionPlataformaCaeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretoApi = table.Column<string>(type: "text", nullable: false),
                    ActualizadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesConexionPlataformaCae", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CredencialesConexionPlataformaCae_ConexionesPlataformaCae_T~",
                        columns: x => new { x.TenantId, x.ConexionPlataformaCaeId },
                        principalTable: "ConexionesPlataformaCae",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsosConexionPlataformaCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConexionPlataformaCaeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operacion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Resultado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Motivo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcreditacionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaveIdempotencia = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IniciadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinalizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsosConexionPlataformaCae", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsosConexionPlataformaCae_ConexionesPlataformaCae_TenantId_~",
                        columns: x => new { x.TenantId, x.ConexionPlataformaCaeId },
                        principalTable: "ConexionesPlataformaCae",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000002"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000006"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000007"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000008"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000009"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000001"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000002"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000003"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000004"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000005"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000006"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000007"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000008"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000a-0000-0000-0000-000000000009"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000001"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000002"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000003"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000004"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000005"),
                column: "ConectorApiActivo",
                value: false);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "AddOnSubidaAutomaticaActivo",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_ConexionesPlataformaCae_ProveedorPlataformaCaeId",
                table: "ConexionesPlataformaCae",
                column: "ProveedorPlataformaCaeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConexionesPlataformaCae_TenantId_Nombre",
                table: "ConexionesPlataformaCae",
                columns: new[] { "TenantId", "Nombre" },
                unique: true,
                filter: "\"EstaEliminado\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesConexionPlataformaCae_TenantId_ConexionPlatafor~",
                table: "CredencialesConexionPlataformaCae",
                columns: new[] { "TenantId", "ConexionPlataformaCaeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsosConexionPlataformaCae_TenantId_ConexionPlataformaCaeId_~",
                table: "UsosConexionPlataformaCae",
                columns: new[] { "TenantId", "ConexionPlataformaCaeId", "IniciadoEnUtc" });

            // Permisos explícitos: ALTER DEFAULT PRIVILEGES da a toda tabla nueva SELECT a cae_app_soporte y
            // CRUD a cae_app_runtime, y aquí no sirve ninguno de los dos.
            //
            // CredencialesConexionPlataformaCae: la credencial de API de un acuerdo con una plataforma CAE de
            // terceros. Soporte TALVEG no tiene ningún permiso, ni sobre la columna cifrada ni sobre sus
            // metadatos (OWASP ASVS 4.0.3 V2.10.4, V4.1.3).
            //
            // UsosConexionPlataformaCae: sin DELETE y con UPDATE solo de las tres columnas del cierre; la
            // política restrictiva impide reescribir un uso ya cerrado o insertar uno que finja una llamada
            // exitosa, y los CHECK fijan el enumerado y la coherencia del cierre (V7.3.3).
            migrationBuilder.Sql(
                """
                ALTER TABLE public."ConexionesPlataformaCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ConexionesPlataformaCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ConexionesPlataformaCae"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."ConexionesPlataformaCae" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."ConexionesPlataformaCae" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."ConexionesPlataformaCae" TO cae_app_soporte;

                ALTER TABLE public."CredencialesConexionPlataformaCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CredencialesConexionPlataformaCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CredencialesConexionPlataformaCae"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."CredencialesConexionPlataformaCae" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."CredencialesConexionPlataformaCae" TO cae_app_runtime;

                ALTER TABLE public."UsosConexionPlataformaCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."UsosConexionPlataformaCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."UsosConexionPlataformaCae"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                CREATE POLICY solo_se_cierra_una_vez ON public."UsosConexionPlataformaCae"
                    AS RESTRICTIVE FOR UPDATE
                    USING ("Resultado" = 'EnCurso')
                    WITH CHECK ("Resultado" <> 'EnCurso' AND "FinalizadoEnUtc" IS NOT NULL);
                CREATE POLICY solo_nace_abierto_o_denegado ON public."UsosConexionPlataformaCae"
                    AS RESTRICTIVE FOR INSERT
                    WITH CHECK ("Resultado" IN ('EnCurso', 'Denegado'));
                ALTER TABLE public."UsosConexionPlataformaCae"
                    ADD CONSTRAINT "CK_UsosConexionPlataformaCae_Resultado"
                        CHECK ("Resultado" IN ('EnCurso', 'Exito', 'Fallo', 'Denegado')),
                    ADD CONSTRAINT "CK_UsosConexionPlataformaCae_Cierre"
                        CHECK (("Resultado" = 'EnCurso') = ("FinalizadoEnUtc" IS NULL)),
                    ADD CONSTRAINT "CK_UsosConexionPlataformaCae_Motivo"
                        CHECK (("Resultado" IN ('Denegado', 'Fallo')) = ("Motivo" IS NOT NULL));
                REVOKE ALL ON TABLE public."UsosConexionPlataformaCae" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."UsosConexionPlataformaCae" TO cae_app_runtime;
                GRANT UPDATE ("Resultado", "Motivo", "FinalizadoEnUtc") ON TABLE public."UsosConexionPlataformaCae" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."UsosConexionPlataformaCae" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CredencialesConexionPlataformaCae");

            migrationBuilder.DropTable(
                name: "UsosConexionPlataformaCae");

            migrationBuilder.DropTable(
                name: "ConexionesPlataformaCae");

            migrationBuilder.DropColumn(
                name: "AddOnSubidaAutomaticaActivo",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ConectorApiActivo",
                table: "ProveedoresPlataformaCae");
        }
    }
}
