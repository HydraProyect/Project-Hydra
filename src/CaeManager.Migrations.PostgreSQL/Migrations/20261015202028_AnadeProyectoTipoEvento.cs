using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeProyectoTipoEvento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Tipo",
                table: "Proyectos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PlantillasRequisitosEvento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantillasRequisitosEvento", x => x.Id);
                    table.UniqueConstraint("AK_PlantillasRequisitosEvento_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitosEvento_Empresas_TenantId_ClienteId",
                        columns: x => new { x.TenantId, x.ClienteId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProyectosRequisitos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProyectoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProyectosRequisitos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProyectosRequisitos_Proyectos_TenantId_ProyectoId",
                        columns: x => new { x.TenantId, x.ProyectoId },
                        principalTable: "Proyectos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProyectosRequisitos_TiposDocumento_TenantId_TipoDocumentoId",
                        columns: x => new { x.TenantId, x.TipoDocumentoId },
                        principalTable: "TiposDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlantillasRequisitosEventoTipos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantillaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantillasRequisitosEventoTipos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitosEventoTipos_PlantillasRequisitosEvento_~",
                        columns: x => new { x.TenantId, x.PlantillaId },
                        principalTable: "PlantillasRequisitosEvento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlantillasRequisitosEventoTipos_TiposDocumento_TenantId_Tip~",
                        columns: x => new { x.TenantId, x.TipoDocumentoId },
                        principalTable: "TiposDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitosEvento_TenantId_ClienteId_Nombre",
                table: "PlantillasRequisitosEvento",
                columns: new[] { "TenantId", "ClienteId", "Nombre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitosEventoTipos_TenantId_PlantillaId_TipoDo~",
                table: "PlantillasRequisitosEventoTipos",
                columns: new[] { "TenantId", "PlantillaId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasRequisitosEventoTipos_TenantId_TipoDocumentoId",
                table: "PlantillasRequisitosEventoTipos",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosRequisitos_TenantId_ProyectoId_TipoDocumentoId",
                table: "ProyectosRequisitos",
                columns: new[] { "TenantId", "ProyectoId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosRequisitos_TenantId_TipoDocumentoId",
                table: "ProyectosRequisitos",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            // Misma protección que las tablas hermanas (ProyectosTecnicos, TiposDocumentoCentros): RLS forzada
            // con la política aislamiento_tenant y los permisos de cae_app_runtime y cae_app_soporte.
            foreach (var tabla in new[] { "ProyectosRequisitos", "PlantillasRequisitosEvento", "PlantillasRequisitosEventoTipos" })
            {
                migrationBuilder.Sql(
                    $"""
                    ALTER TABLE public."{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE ONLY public."{tabla}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY aislamiento_tenant ON public."{tabla}" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                    GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."{tabla}" TO cae_app_runtime;
                    GRANT SELECT ON TABLE public."{tabla}" TO cae_app_soporte;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlantillasRequisitosEventoTipos");

            migrationBuilder.DropTable(
                name: "ProyectosRequisitos");

            migrationBuilder.DropTable(
                name: "PlantillasRequisitosEvento");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "Proyectos");
        }
    }
}
