using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeFichaVehiculo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Aseguradora",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclaracionCe",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Poliza",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recibo",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevisionGrua",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tipo",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Titularidad",
                table: "Vehiculos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VehiculosTrabajadores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiculoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiculosTrabajadores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehiculosTrabajadores_Trabajadores_TenantId_TrabajadorId",
                        columns: x => new { x.TenantId, x.TrabajadorId },
                        principalTable: "Trabajadores",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehiculosTrabajadores_Vehiculos_TenantId_VehiculoId",
                        columns: x => new { x.TenantId, x.VehiculoId },
                        principalTable: "Vehiculos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VehiculosTrabajadores_TenantId_TrabajadorId",
                table: "VehiculosTrabajadores",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_VehiculosTrabajadores_TenantId_VehiculoId_TrabajadorId",
                table: "VehiculosTrabajadores",
                columns: new[] { "TenantId", "VehiculoId", "TrabajadorId" },
                unique: true);

            // Misma protección que la tabla hermana VisitasTrabajadores: RLS forzada con la
            // política aislamiento_tenant y los permisos de cae_app_runtime y cae_app_soporte.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."VehiculosTrabajadores" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."VehiculosTrabajadores" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."VehiculosTrabajadores" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."VehiculosTrabajadores" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."VehiculosTrabajadores" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VehiculosTrabajadores");

            migrationBuilder.DropColumn(
                name: "Aseguradora",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "DeclaracionCe",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "Poliza",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "Recibo",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "RevisionGrua",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "Titularidad",
                table: "Vehiculos");
        }
    }
}
