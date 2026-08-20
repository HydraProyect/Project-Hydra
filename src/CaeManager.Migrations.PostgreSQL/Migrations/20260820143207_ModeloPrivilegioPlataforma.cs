using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ModeloPrivilegioPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConcesionesPrivilegio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioPlataformaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capacidad = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EsAlcanceGlobal = table.Column<bool>(type: "boolean", nullable: false),
                    VigenciaDesde = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenciaHasta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConcedidaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    MotivoConcesion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcesionesPrivilegio", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SesionesPrivilegiadas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcesionPrivilegioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantObjetivoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioSimuladoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Ticket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InicioEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiraEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CerradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SesionesPrivilegiadas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SesionesPrivilegiadas_ConcesionesPrivilegio_ConcesionPrivil~",
                        column: x => x.ConcesionPrivilegioId,
                        principalTable: "ConcesionesPrivilegio",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantsAlcanzadosPorConcesion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcesionPrivilegioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantsAlcanzadosPorConcesion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantsAlcanzadosPorConcesion_ConcesionesPrivilegio_Concesi~",
                        column: x => x.ConcesionPrivilegioId,
                        principalTable: "ConcesionesPrivilegio",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConcesionesPrivilegio_UsuarioPlataformaId_Estado",
                table: "ConcesionesPrivilegio",
                columns: new[] { "UsuarioPlataformaId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_SesionesPrivilegiadas_Abiertas",
                table: "SesionesPrivilegiadas",
                column: "ExpiraEnUtc",
                filter: "\"CerradaEnUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SesionesPrivilegiadas_ConcesionPrivilegioId",
                table: "SesionesPrivilegiadas",
                column: "ConcesionPrivilegioId");

            migrationBuilder.CreateIndex(
                name: "IX_SesionesPrivilegiadas_TenantObjetivoId_InicioEnUtc",
                table: "SesionesPrivilegiadas",
                columns: new[] { "TenantObjetivoId", "InicioEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantsAlcanzadosPorConcesion_ConcesionPrivilegioId_TenantId",
                table: "TenantsAlcanzadosPorConcesion",
                columns: new[] { "ConcesionPrivilegioId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantsAlcanzadosPorConcesion_TenantId",
                table: "TenantsAlcanzadosPorConcesion",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SesionesPrivilegiadas");

            migrationBuilder.DropTable(
                name: "TenantsAlcanzadosPorConcesion");

            migrationBuilder.DropTable(
                name: "ConcesionesPrivilegio");
        }
    }
}
