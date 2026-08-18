using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAsignacionesOperativas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AsignacionesOperacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EsRaiz = table.Column<bool>(type: "boolean", nullable: false),
                    Servicio = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmbitoRelacionClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmbitoCentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmbitoTrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmbitoProyectoId = table.Column<Guid>(type: "uuid", nullable: true),
                    VigenciaDesde = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenciaHasta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MotivoCierre = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsignacionesOperacion", x => x.Id);
                    table.UniqueConstraint("AK_AsignacionesOperacion_Id_PropietarioTenantId", x => new { x.Id, x.PropietarioTenantId });
                    table.ForeignKey(
                        name: "FK_AsignacionesOperacion_Centros_PropietarioTenantId_AmbitoCen~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoCentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesOperacion_Clientes_PropietarioTenantId_AmbitoRe~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoRelacionClienteId },
                        principalTable: "Clientes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesOperacion_Proyectos_PropietarioTenantId_AmbitoP~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoProyectoId },
                        principalTable: "Proyectos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesOperacion_Trabajadores_PropietarioTenantId_Ambi~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoTrabajadorId },
                        principalTable: "Trabajadores",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AsignacionesCartera",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AsignacionOperacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmbitoRelacionClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmbitoCentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmbitoTrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmbitoProyectoId = table.Column<Guid>(type: "uuid", nullable: true),
                    VigenciaDesde = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenciaHasta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MotivoCierre = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsignacionesCartera", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsignacionesCartera_AsignacionesOperacion_AsignacionOperaci~",
                        columns: x => new { x.AsignacionOperacionId, x.PropietarioTenantId },
                        principalTable: "AsignacionesOperacion",
                        principalColumns: new[] { "Id", "PropietarioTenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesCartera_Centros_PropietarioTenantId_AmbitoCentr~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoCentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesCartera_Clientes_PropietarioTenantId_AmbitoRela~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoRelacionClienteId },
                        principalTable: "Clientes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesCartera_Proyectos_PropietarioTenantId_AmbitoPro~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoProyectoId },
                        principalTable: "Proyectos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AsignacionesCartera_Trabajadores_PropietarioTenantId_Ambito~",
                        columns: x => new { x.PropietarioTenantId, x.AmbitoTrabajadorId },
                        principalTable: "Trabajadores",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_AsignacionOperacionId_PropietarioTenant~",
                table: "AsignacionesCartera",
                columns: new[] { "AsignacionOperacionId", "PropietarioTenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoCentroId",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoCentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoProyectoId",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoProyectoId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoRelacionClien~",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoTrabajadorId",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoTrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera",
                columns: new[] { "AsignacionOperacionId", "AmbitoRelacionClienteId" },
                unique: true,
                filter: "\"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NOT NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_UsuarioId_Estado",
                table: "AsignacionesCartera",
                columns: new[] { "UsuarioId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_UsuarioUniversalVigente",
                table: "AsignacionesCartera",
                columns: new[] { "AsignacionOperacionId", "UsuarioId" },
                unique: true,
                filter: "\"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_AmbitoRelacionCliente",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                filter: "\"AmbitoRelacionClienteId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_DelegacionTotalVigente",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "Servicio" },
                unique: true,
                filter: "NOT \"EsRaiz\" AND \"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_OperadorTenantId_Estado",
                table: "AsignacionesOperacion",
                columns: new[] { "OperadorTenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_PropietarioTenantId_AmbitoCentroId",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "AmbitoCentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_PropietarioTenantId_AmbitoProyectoId",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "AmbitoProyectoId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_PropietarioTenantId_AmbitoTrabajadorId",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "AmbitoTrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_PropietarioTenantId_Servicio_Estado",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "Servicio", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_RaizVigente",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "Servicio" },
                unique: true,
                filter: "\"EsRaiz\" AND \"Estado\" = 'Vigente'");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperacion_ResponsableRelacionVigente",
                table: "AsignacionesOperacion",
                columns: new[] { "PropietarioTenantId", "Servicio", "AmbitoRelacionClienteId" },
                unique: true,
                filter: "\"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NOT NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsignacionesCartera");

            migrationBuilder.DropTable(
                name: "AsignacionesOperacion");
        }
    }
}
