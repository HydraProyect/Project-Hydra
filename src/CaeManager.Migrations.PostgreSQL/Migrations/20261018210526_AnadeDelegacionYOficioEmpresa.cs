using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeDelegacionYOficioEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId",
                table: "Trabajadores");

            migrationBuilder.AddColumn<Guid>(
                name: "DelegacionEmpresaId",
                table: "Trabajadores",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OficioEmpresaId",
                table: "Trabajadores",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DelegacionesEmpresa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ContactoReclamacionNombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContactoReclamacionCorreo = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ContactoReclamacionTelefono = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelegacionesEmpresa", x => x.Id);
                    table.UniqueConstraint("AK_DelegacionesEmpresa_TenantId_EmpresaId_Id", x => new { x.TenantId, x.EmpresaId, x.Id });
                    table.ForeignKey(
                        name: "FK_DelegacionesEmpresa_Empresas_TenantId_EmpresaId",
                        columns: x => new { x.TenantId, x.EmpresaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OficiosEmpresa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_OficiosEmpresa", x => x.Id);
                    table.UniqueConstraint("AK_OficiosEmpresa_TenantId_EmpresaId_Id", x => new { x.TenantId, x.EmpresaId, x.Id });
                    table.ForeignKey(
                        name: "FK_OficiosEmpresa_Empresas_TenantId_EmpresaId",
                        columns: x => new { x.TenantId, x.EmpresaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId_DelegacionEmpresaId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId", "DelegacionEmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId_OficioEmpresaId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId", "OficioEmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_DelegacionesEmpresa_TenantId_EmpresaId",
                table: "DelegacionesEmpresa",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_OficiosEmpresa_TenantId_EmpresaId",
                table: "OficiosEmpresa",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Trabajadores_DelegacionesEmpresa_TenantId_EmpresaId_Delegac~",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId", "DelegacionEmpresaId" },
                principalTable: "DelegacionesEmpresa",
                principalColumns: new[] { "TenantId", "EmpresaId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Trabajadores_OficiosEmpresa_TenantId_EmpresaId_OficioEmpres~",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId", "OficioEmpresaId" },
                principalTable: "OficiosEmpresa",
                principalColumns: new[] { "TenantId", "EmpresaId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Un Trabajador de Subcontrata (EmpresaId nulo) no tiene Delegación ni Oficio: con EmpresaId nulo la FK
            // compuesta es MATCH SIMPLE y no comprobaría nada, así que lo impide esta restricción.
            migrationBuilder.Sql("""
                ALTER TABLE "Trabajadores" ADD CONSTRAINT "CK_Trabajadores_CatalogoSoloDeEmpresa"
                    CHECK (("DelegacionEmpresaId" IS NULL AND "OficioEmpresaId" IS NULL) OR "EmpresaId" IS NOT NULL);
                """);

            // Migración de datos (decisión de Chris, 2026-10-18): un Oficio por cada Puesto distinto de cada Empresa,
            // sin distinguir mayúsculas ni espacios (mismo criterio que NombreDeCatalogo.Clave), y cada Trabajador de
            // Empresa con Puesto queda con ese Oficio. El Puesto se conserva. Quedan fuera los Trabajadores de
            // Subcontrata, los eliminados y los anonimizados (su Puesto ya es nulo). Como las demás migraciones de
            // datos sobre Trabajadores (AnadeTipoContratacionTrabajador), corre con la identidad del migrador. El Id es
            // determinista (md5 de Tenant, Empresa y clave), como en ConvierteCarterasPorClienteATenantEntero: no depende
            // de uuidv7(), que no existe en PostgreSQL 17, y gen_random_uuid() lo prohíbe IdentificadoresDeEntidadUuidV7Tests.
            migrationBuilder.Sql("""
                INSERT INTO public."OficiosEmpresa" ("Id", "TenantId", "EmpresaId", "Nombre", "Version", "CreadoEnUtc", "EstaEliminado")
                SELECT md5('oficio:' || g."TenantId"::text || ':' || g."EmpresaId"::text || ':' || g.clave)::uuid,
                       g."TenantId", g."EmpresaId", g."Nombre", md5(random()::text || clock_timestamp()::text)::uuid, now(), false
                FROM (
                    SELECT t."TenantId", t."EmpresaId", lower(regexp_replace(btrim(t."Puesto"), '\s+', ' ', 'g')) AS clave,
                           min(regexp_replace(btrim(t."Puesto"), '\s+', ' ', 'g')) AS "Nombre"
                    FROM public."Trabajadores" t
                    WHERE t."EmpresaId" IS NOT NULL
                      AND NOT t."EstaEliminado"
                      AND t."AnonimizadoEnUtc" IS NULL
                      AND btrim(COALESCE(t."Puesto", '')) <> ''
                    GROUP BY t."TenantId", t."EmpresaId", clave
                ) g;

                UPDATE public."Trabajadores" t
                   SET "OficioEmpresaId" = o."Id"
                  FROM public."OficiosEmpresa" o
                 WHERE o."TenantId" = t."TenantId"
                   AND o."EmpresaId" = t."EmpresaId"
                   AND t."AnonimizadoEnUtc" IS NULL
                   AND lower(o."Nombre") = lower(regexp_replace(btrim(t."Puesto"), '\s+', ' ', 'g'));
                """);

            // Aislamiento por Tenant de las dos tablas nuevas, igual que el resto de tablas con TenantId. Va después de la
            // migración de datos para que el INSERT de los Oficios no dependa de que el migrador salte la RLS forzada.
            foreach (var tabla in new[] { "DelegacionesEmpresa", "OficiosEmpresa" })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE public."{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE public."{tabla}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY aislamiento_tenant ON public."{tabla}"
                        USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."{tabla}" TO cae_app_runtime;
                    GRANT SELECT ON TABLE public."{tabla}" TO cae_app_soporte;
                    """);
            }

            // Trabajador.OficioEmpresaId dice lo mismo que el Puesto: entra en Trabajador.CamposDeDatosPersonales y la
            // función que enmascara el historial de un Trabajador anonimizado se redefine con él (mismo cuerpo).
            migrationBuilder.Sql(AnadeContactoEmergenciaATrabajador.DefinicionDeLaFuncion(CamposConOficio));
        }

        private const string CamposConOficio =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Notas',
                                'ContactoEmergenciaNombre', 'ContactoEmergenciaTelefono', 'Puesto', 'OficioEmpresaId',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AnadeContactoEmergenciaATrabajador.DefinicionDeLaFuncion(
                AnadeContactoEmergenciaATrabajador.CamposConContactoEmergencia));

            migrationBuilder.Sql("""ALTER TABLE "Trabajadores" DROP CONSTRAINT "CK_Trabajadores_CatalogoSoloDeEmpresa";""");
            migrationBuilder.DropForeignKey(
                name: "FK_Trabajadores_DelegacionesEmpresa_TenantId_EmpresaId_Delegac~",
                table: "Trabajadores");

            migrationBuilder.DropForeignKey(
                name: "FK_Trabajadores_OficiosEmpresa_TenantId_EmpresaId_OficioEmpres~",
                table: "Trabajadores");

            migrationBuilder.DropTable(
                name: "DelegacionesEmpresa");

            migrationBuilder.DropTable(
                name: "OficiosEmpresa");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId_DelegacionEmpresaId",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId_OficioEmpresaId",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "DelegacionEmpresaId",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "OficioEmpresaId",
                table: "Trabajadores");

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId" });
        }
    }
}
