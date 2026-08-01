using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Cola durable de análisis IA (P2 #22 de docs/business/MATURITY_REVIEW.md
    /// — reemplaza la cola en memoria sobre <c>Channel&lt;T&gt;</c>, que
    /// perdía los encargos pendientes en cada reinicio del proceso). Crea
    /// <c>TrabajosAnalisisDocumento</c> con RLS igual que el resto de tablas
    /// con <c>TenantId</c> (ver <c>HabilitarRlsPostgres</c>): el rol
    /// <c>cae_app_runtime</c> ya tiene los privilegios de DML sobre esta
    /// tabla nueva vía el <c>ALTER DEFAULT PRIVILEGES</c> de esa migración
    /// (aplica automáticamente a cualquier tabla que cree el mismo rol
    /// propietario después) — lo que <c>ALTER DEFAULT PRIVILEGES</c> no cubre
    /// es habilitar RLS en sí, así que esta migración lo hace explícitamente
    /// para la tabla nueva.
    /// </summary>
    public partial class AgregarTrabajosAnalisisDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrabajosAnalisisDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioSolicitanteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tipo = table.Column<string>(type: "text", nullable: false),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    UltimoError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IniciadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrabajosAnalisisDocumento", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosAnalisisDocumento_TenantId_Estado_CreadoEnUtc",
                table: "TrabajosAnalisisDocumento",
                columns: new[] { "TenantId", "Estado", "CreadoEnUtc" });

            migrationBuilder.Sql(@"
ALTER TABLE ""TrabajosAnalisisDocumento"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""TrabajosAnalisisDocumento"" FORCE ROW LEVEL SECURITY;
CREATE POLICY aislamiento_tenant ON ""TrabajosAnalisisDocumento""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP POLICY IF EXISTS aislamiento_tenant ON ""TrabajosAnalisisDocumento"";");

            migrationBuilder.DropTable(name: "TrabajosAnalisisDocumento");
        }
    }
}
