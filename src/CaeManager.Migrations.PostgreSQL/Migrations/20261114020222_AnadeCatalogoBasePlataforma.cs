using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCatalogoBasePlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogoActividades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Codigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogoActividades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogoRequisitosBase",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Codigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Fundamento = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Nivel = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogoRequisitosBase", x => x.Id);
                    table.CheckConstraint("CK_CatalogoRequisitosBase_Nivel", "\"Nivel\" IN ('N1', 'N2', 'N3')");
                });

            migrationBuilder.CreateTable(
                name: "CatalogoRiesgos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Codigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EsOtro = table.Column<bool>(type: "boolean", nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogoRiesgos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogoActividadesCodigosOficiales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActividadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sistema = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Codigo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogoActividadesCodigosOficiales", x => x.Id);
                    table.CheckConstraint("CK_CatalogoActividadesCodigosOficiales_CodigoFormato", "\"Codigo\" ~ '^[A-Za-z0-9.]+$'");
                    table.CheckConstraint("CK_CatalogoActividadesCodigosOficiales_Sistema", "\"Sistema\" IN ('Cnae2025', 'Regcon', 'Cno11')");
                    table.ForeignKey(
                        name: "FK_CatalogoActividadesCodigosOficiales_CatalogoActividades_Act~",
                        column: x => x.ActividadId,
                        principalTable: "CatalogoActividades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogoReglasRiesgoRequisito",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RiesgoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequisitoId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogoReglasRiesgoRequisito", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogoReglasRiesgoRequisito_CatalogoRequisitosBase_Requis~",
                        column: x => x.RequisitoId,
                        principalTable: "CatalogoRequisitosBase",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogoReglasRiesgoRequisito_CatalogoRiesgos_RiesgoId",
                        column: x => x.RiesgoId,
                        principalTable: "CatalogoRiesgos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoActividades_Codigo",
                table: "CatalogoActividades",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoActividadesCodigosOficiales_ActividadId_Sistema_Cod~",
                table: "CatalogoActividadesCodigosOficiales",
                columns: new[] { "ActividadId", "Sistema", "Codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoActividadesCodigosOficiales_Sistema_Codigo",
                table: "CatalogoActividadesCodigosOficiales",
                columns: new[] { "Sistema", "Codigo" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoReglasRiesgoRequisito_RequisitoId",
                table: "CatalogoReglasRiesgoRequisito",
                column: "RequisitoId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoReglasRiesgoRequisito_RiesgoId_RequisitoId",
                table: "CatalogoReglasRiesgoRequisito",
                columns: new[] { "RiesgoId", "RequisitoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoRequisitosBase_Codigo",
                table: "CatalogoRequisitosBase",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoRiesgos_Codigo",
                table: "CatalogoRiesgos",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoRiesgos_UnaSolaOpcionOtro",
                table: "CatalogoRiesgos",
                column: "EsOtro",
                unique: true,
                filter: "\"EsOtro\"");

            // El catálogo base de Plataforma (B-13; CA-2, CA-4, CA-7) es global: el mismo para todos los Tenants, sin
            // TenantId ni RLS, como FestivosOficiales. Es la excepción a RLS documentada en
            // CoberturaRlsDelModeloTests.CatalogoBaseDePlataformaSinRls. Lo que lo protege es el privilegio: el
            // runtime solo lo lee, y soporte, aprovisionamiento y PUBLIC no tienen nada. Ningún rol de la aplicación
            // lo escribe; quién lo mantiene (un Actor de Plataforma TALVEG) llega en otro incremento. Sin datos: el
            // contenido espera a la revisión de un técnico habilitado (CA-7).
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."CatalogoActividades", public."CatalogoActividadesCodigosOficiales",
                    public."CatalogoRiesgos", public."CatalogoRequisitosBase", public."CatalogoReglasRiesgoRequisito"
                    FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."CatalogoActividades", public."CatalogoActividadesCodigosOficiales",
                    public."CatalogoRiesgos", public."CatalogoRequisitosBase", public."CatalogoReglasRiesgoRequisito"
                    TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogoActividadesCodigosOficiales");

            migrationBuilder.DropTable(
                name: "CatalogoReglasRiesgoRequisito");

            migrationBuilder.DropTable(
                name: "CatalogoActividades");

            migrationBuilder.DropTable(
                name: "CatalogoRequisitosBase");

            migrationBuilder.DropTable(
                name: "CatalogoRiesgos");
        }
    }
}
