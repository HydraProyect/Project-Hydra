using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

public partial class SoporteSinJsonSensibles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE public."DocumentosGenerados" FROM cae_app_soporte;
            GRANT SELECT ("Id", "CentroId", "CreadoEnUtc", "DocumentoId", "EliminadoEnUtc",
                          "EliminadoPorUsuarioId", "EmpresaId", "EstaEliminado", "Estado", "GeneradoEnUtc",
                          "GeneradoPorUsuarioId", "HashSha256Impreso", "PlantillaDocumentoVersionId",
                          "SelloElectronicoAplicadoEnUtc", "TenantId", "TrabajadorId", "Version")
                ON TABLE public."DocumentosGenerados" TO cae_app_soporte;
            REVOKE ALL ON TABLE public."LotesGeneracionDocumento" FROM cae_app_soporte;
            GRANT SELECT ("Id", "PlantillaDocumentoVersionId", "Estado", "TotalItems", "ItemsCompletados",
                          "ItemsFallidos", "UsuarioSolicitanteId", "CreadoEnUtc", "CompletadoEnUtc", "TenantId")
                ON TABLE public."LotesGeneracionDocumento" TO cae_app_soporte;
            REVOKE ALL ON TABLE public."SolicitudesConexionMicrosoft365" FROM cae_app_soporte;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE public."DocumentosGenerados" FROM cae_app_soporte;
            GRANT SELECT ("Id", "CentroId", "CreadoEnUtc", "DatosUtilizadosJson", "DocumentoId",
                          "EliminadoEnUtc", "EliminadoPorUsuarioId", "EmpresaId", "EstaEliminado", "Estado",
                          "GeneradoEnUtc", "GeneradoPorUsuarioId", "HashSha256Impreso", "PlantillaDocumentoVersionId",
                          "SelloElectronicoAplicadoEnUtc", "TenantId", "TrabajadorId", "Version")
                ON TABLE public."DocumentosGenerados" TO cae_app_soporte;
            REVOKE ALL ON TABLE public."LotesGeneracionDocumento" FROM cae_app_soporte;
            GRANT SELECT ON TABLE public."LotesGeneracionDocumento" TO cae_app_soporte;
            GRANT SELECT ON TABLE public."SolicitudesConexionMicrosoft365" TO cae_app_soporte;
            """);
    }
}
