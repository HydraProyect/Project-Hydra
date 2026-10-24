using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEstadoMaterializado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EstadosEntidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ambito = table.Column<short>(type: "smallint", nullable: false),
                    EntidadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<short>(type: "smallint", nullable: false),
                    EstadoPlataforma = table.Column<short>(type: "smallint", nullable: true),
                    Vencidos = table.Column<int>(type: "integer", nullable: false),
                    VencidosSinBloqueo = table.Column<int>(type: "integer", nullable: false),
                    Urgentes = table.Column<int>(type: "integer", nullable: false),
                    Proximos = table.Column<int>(type: "integer", nullable: false),
                    Faltan = table.Column<int>(type: "integer", nullable: false),
                    SinConfirmar = table.Column<int>(type: "integer", nullable: false),
                    PendientesEnPlataforma = table.Column<int>(type: "integer", nullable: false),
                    Requeridos = table.Column<int>(type: "integer", nullable: false),
                    AlDia = table.Column<int>(type: "integer", nullable: false),
                    ProximoCambioEl = table.Column<DateOnly>(type: "date", nullable: true),
                    CalculadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VersionCalculo = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstadosEntidad", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EstadosPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanalGestionDocumentalId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmbitoTitular = table.Column<short>(type: "smallint", nullable: false),
                    TitularId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<short>(type: "smallint", nullable: false),
                    EstadoVigencia = table.Column<short>(type: "smallint", nullable: false),
                    FechaVencimientoEnPlataforma = table.Column<DateOnly>(type: "date", nullable: true),
                    ProximoCambioEl = table.Column<DateOnly>(type: "date", nullable: true),
                    CalculadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VersionCalculo = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstadosPlataforma", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EstadosRequisito",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmbitoTitular = table.Column<short>(type: "smallint", nullable: false),
                    TitularId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoEfectivoId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstadoTalveg = table.Column<short>(type: "smallint", nullable: false),
                    BloqueaAcceso = table.Column<bool>(type: "boolean", nullable: false),
                    CuentaParaCumplimiento = table.Column<bool>(type: "boolean", nullable: false),
                    EstadoPlataforma = table.Column<short>(type: "smallint", nullable: true),
                    VencimientoEfectivo = table.Column<DateOnly>(type: "date", nullable: true),
                    ProximoCambioEl = table.Column<DateOnly>(type: "date", nullable: true),
                    CalculadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VersionCalculo = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstadosRequisito", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecalculosPendientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ambito = table.Column<short>(type: "smallint", nullable: false),
                    EntidadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Motivo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntentoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecalculosPendientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VersionesEstadoTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ActualizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionesEstadoTenant", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EstadosEntidad_Estado",
                table: "EstadosEntidad",
                columns: new[] { "TenantId", "Ambito", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_EstadosEntidad_ProximoCambio",
                table: "EstadosEntidad",
                columns: new[] { "TenantId", "ProximoCambioEl" });

            migrationBuilder.CreateIndex(
                name: "UX_EstadosEntidad_Entidad",
                table: "EstadosEntidad",
                columns: new[] { "TenantId", "Ambito", "EntidadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EstadosPlataforma_ProximoCambio",
                table: "EstadosPlataforma",
                columns: new[] { "TenantId", "ProximoCambioEl" });

            migrationBuilder.CreateIndex(
                name: "IX_EstadosPlataforma_Titular",
                table: "EstadosPlataforma",
                columns: new[] { "TenantId", "AmbitoTitular", "TitularId" });

            migrationBuilder.CreateIndex(
                name: "UX_EstadosPlataforma_Acreditacion",
                table: "EstadosPlataforma",
                columns: new[] { "TenantId", "CentroId", "CanalGestionDocumentalId", "AmbitoTitular", "TitularId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EstadosRequisito_EstadoTalveg",
                table: "EstadosRequisito",
                columns: new[] { "TenantId", "EstadoTalveg" });

            migrationBuilder.CreateIndex(
                name: "IX_EstadosRequisito_ProximoCambio",
                table: "EstadosRequisito",
                columns: new[] { "TenantId", "ProximoCambioEl" });

            migrationBuilder.CreateIndex(
                name: "IX_EstadosRequisito_Titular",
                table: "EstadosRequisito",
                columns: new[] { "TenantId", "AmbitoTitular", "TitularId" });

            migrationBuilder.CreateIndex(
                name: "UX_EstadosRequisito_Par",
                table: "EstadosRequisito",
                columns: new[] { "TenantId", "CentroId", "AmbitoTitular", "TitularId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecalculosPendientes_Cola",
                table: "RecalculosPendientes",
                columns: new[] { "TenantId", "CreadoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_VersionesEstadoTenant_Tenant",
                table: "VersionesEstadoTenant",
                column: "TenantId",
                unique: true);

            // Las cinco son tablas del Tenant propietario: RLS estándar por TenantId, forzada, con la política
            // aislamiento_tenant (USING y WITH CHECK), como ContratosAcceso. Ningún rol las salta: ninguno tiene
            // BYPASSRLS (VerificacionIdentidadDeRuntime impide arrancar con uno).
            //
            // Permisos (diseño del estado materializado, § 3.5):
            // - cae_app_runtime: SELECT, INSERT y DELETE (un par desaparece cuando el Centro deja de exigir un Tipo y la cola
            //   se consume borrando), y UPDATE solo de las columnas que el recálculo rehace. Nunca de Id, TenantId ni de la
            //   clave natural de la fila: una fila no cambia de Tenant ni de par, se borra y se inserta otra.
            // - cae_app_aprovisionamiento: lo mismo y bajo la misma política, porque las altas de Centro, Empresa,
            //   Trabajador e importación que una Sesión Privilegiada de Soporte TALVEG ejecuta con ese rol recalcularán en
            //   su transacción (ElevacionEscrituraAprovisionamientoBehavior); sin el permiso se revertirían enteras.
            // - cae_app_soporte: solo SELECT.
            // REVOKE ALL explícito antes de cada GRANT, por si los privilegios por defecto del esquema concedieran algo.
            const string columnasEstadoRequisito =
                "\"DocumentoEfectivoId\", \"EstadoTalveg\", \"BloqueaAcceso\", \"CuentaParaCumplimiento\", \"EstadoPlataforma\", " +
                "\"VencimientoEfectivo\", \"ProximoCambioEl\", \"CalculadoEnUtc\", \"VersionCalculo\"";
            const string columnasEstadoPlataforma =
                "\"Estado\", \"EstadoVigencia\", \"FechaVencimientoEnPlataforma\", \"ProximoCambioEl\", \"CalculadoEnUtc\", \"VersionCalculo\"";
            const string columnasEstadoEntidad =
                "\"Estado\", \"EstadoPlataforma\", \"Vencidos\", \"VencidosSinBloqueo\", \"Urgentes\", \"Proximos\", \"Faltan\", " +
                "\"SinConfirmar\", \"PendientesEnPlataforma\", \"Requeridos\", \"AlDia\", \"ProximoCambioEl\", \"CalculadoEnUtc\", \"VersionCalculo\"";
            const string columnasVersionEstadoTenant = "\"Version\", \"ActualizadoEnUtc\"";
            const string columnasRecalculoPendiente = "\"IntentoEnUtc\", \"Intentos\"";

            foreach (var (tabla, columnas) in new[]
                     {
                         ("EstadosRequisito", columnasEstadoRequisito),
                         ("EstadosPlataforma", columnasEstadoPlataforma),
                         ("EstadosEntidad", columnasEstadoEntidad),
                         ("VersionesEstadoTenant", columnasVersionEstadoTenant),
                         ("RecalculosPendientes", columnasRecalculoPendiente),
                     })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE public."{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE ONLY public."{tabla}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY aislamiento_tenant ON public."{tabla}"
                        USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                        WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                    REVOKE ALL ON TABLE public."{tabla}" FROM cae_app_runtime;
                    GRANT SELECT, INSERT, DELETE ON TABLE public."{tabla}" TO cae_app_runtime;
                    GRANT UPDATE ({columnas}) ON TABLE public."{tabla}" TO cae_app_runtime;
                    REVOKE ALL ON TABLE public."{tabla}" FROM cae_app_aprovisionamiento;
                    GRANT SELECT, INSERT, DELETE ON TABLE public."{tabla}" TO cae_app_aprovisionamiento;
                    GRANT UPDATE ({columnas}) ON TABLE public."{tabla}" TO cae_app_aprovisionamiento;
                    REVOKE ALL ON TABLE public."{tabla}" FROM cae_app_soporte;
                    GRANT SELECT ON TABLE public."{tabla}" TO cae_app_soporte;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EstadosEntidad");

            migrationBuilder.DropTable(
                name: "EstadosPlataforma");

            migrationBuilder.DropTable(
                name: "EstadosRequisito");

            migrationBuilder.DropTable(
                name: "RecalculosPendientes");

            migrationBuilder.DropTable(
                name: "VersionesEstadoTenant");
        }
    }
}
