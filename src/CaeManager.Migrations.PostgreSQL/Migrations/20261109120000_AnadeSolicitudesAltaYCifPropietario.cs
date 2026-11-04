using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSolicitudesAltaYCifPropietario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CifPropietario",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TipoTenant",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_TipoTenant",
                table: "Tenants",
                sql: "\"TipoTenant\" IN ('GestionPropia', 'GestionExterna', 'OperadorCaeExterno')");

            migrationBuilder.CreateTable(
                name: "SolicitudesAlta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Correo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CorreoNormalizado = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HashContrasena = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    HashTokenVerificacion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TokenVerificacionCaducaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IpOrigen = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgentOrigen = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CaducaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UltimoCambioEstadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorreoVerificadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CifNif = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DomicilioFiscal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Telefono = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Tramo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Modalidad = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DeclaraRepresentacion = table.Column<bool>(type: "boolean", nullable: false),
                    DeclaraActividadProfesional = table.Column<bool>(type: "boolean", nullable: false),
                    CheckoutSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StripeCustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TenantIdCreado = table.Column<Guid>(type: "uuid", nullable: true),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DatosPersonalesBorradosEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VersionFila = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesAlta", x => x.Id);
                    table.CheckConstraint("CK_SolicitudesAlta_CompletadaConTenant", "(\"Estado\" <> 'Completada') OR (\"TenantIdCreado\" IS NOT NULL)");
                    table.CheckConstraint("CK_SolicitudesAlta_CorreoSalvoBorrado", "(\"Correo\" IS NOT NULL) OR (\"DatosPersonalesBorradosEnUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_SolicitudesAlta_Estado", "\"Estado\" IN ('Iniciada', 'CorreoVerificado', 'DerivadaAAcceso', 'DatosCompletos', 'PendientePago', 'Pagada', 'Completada', 'Reembolsada', 'Caducada', 'Bloqueada')");
                    table.CheckConstraint("CK_SolicitudesAlta_Modalidad", "\"Modalidad\" IN ('Mensual', 'Anual')");
                    table.CheckConstraint("CK_SolicitudesAlta_Tramo", "\"Tramo\" IN ('De1A10', 'De11A40', 'De41A80', 'De81A150', 'De151A300')");
                });

            migrationBuilder.CreateTable(
                name: "AceptacionesSolicitudAlta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitudAltaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Documento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeclaraFacultades = table.Column<bool>(type: "boolean", nullable: false),
                    Ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    AceptadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AceptacionesSolicitudAlta", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AceptacionesSolicitudAlta_SolicitudesAlta_SolicitudAltaId",
                        column: x => x.SolicitudAltaId,
                        principalTable: "SolicitudesAlta",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Sin UpdateData del Tenant sembrado: las dos columnas nuevas nacen null en todas las filas, también
            // en esa, y el alta autónoma no rellena los Tenants existentes.

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_CifPropietario",
                table: "Tenants",
                column: "CifPropietario",
                unique: true,
                filter: "\"CifPropietario\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AceptacionesSolicitudAlta_SolicitudAltaId_Documento",
                table: "AceptacionesSolicitudAlta",
                columns: new[] { "SolicitudAltaId", "Documento" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesAlta_Estado_CaducaEnUtc",
                table: "SolicitudesAlta",
                columns: new[] { "Estado", "CaducaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesAlta_HashTokenVerificacion",
                table: "SolicitudesAlta",
                column: "HashTokenVerificacion",
                unique: true,
                filter: "\"HashTokenVerificacion\" IS NOT NULL");

            // Solicitudes de alta autónoma: plano de Plataforma, sin TenantId (el Tenant propietario aún no
            // existe). Esta migración solo crea el almacén; la vía de acceso la abre el incremento del registro.
            //
            // Cerradas por defecto: RLS + FORCE sin ninguna política, y REVOKE ALL a PUBLIC y a los tres roles
            // de la aplicación, que ALTER DEFAULT PRIVILEGES de la línea base dejaría con CRUD (runtime) y
            // SELECT (soporte). Ningún rol de la aplicación las lee ni las escribe: solo el rol propietario de
            // las tablas (migraciones e identidad de arranque). Guardan el hash de la contraseña y del token
            // de verificación, el correo, la IP y el NIF de una persona física: Soporte TALVEG no lee ninguna
            // columna (OWASP ASVS 4.0.3 V4.1.3, V8.3.4).
            //
            // Compatible con la ranura anterior durante el relevo: solo añade tablas, columnas nullable y un
            // índice parcial y un CHECK sobre columnas que la versión anterior no escribe (todas nacen null).
            migrationBuilder.Sql(
                """
                ALTER TABLE public."SolicitudesAlta" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."SolicitudesAlta" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON TABLE public."SolicitudesAlta" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;

                ALTER TABLE public."AceptacionesSolicitudAlta" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AceptacionesSolicitudAlta" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON TABLE public."AceptacionesSolicitudAlta" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AceptacionesSolicitudAlta");

            migrationBuilder.DropTable(
                name: "SolicitudesAlta");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_CifPropietario",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_TipoTenant",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CifPropietario",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TipoTenant",
                table: "Tenants");
        }
    }
}
