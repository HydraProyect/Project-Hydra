using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Soporte TALVEG deja de leer los datos personales guardados en JSON (OWASP ASVS 4.0.3 V4.1.3, mínimo
/// privilegio). Sigue a <c>SoporteSinCodigoSeguroDocumentosGenerados</c>: <c>cae_app_soporte</c> pierde
/// <c>DocumentosGenerados.DatosUtilizadosJson</c>, <c>LotesGeneracionDocumento.ContextoJson</c>,
/// <c>ExtraccionesIaCache.ExtraccionJson</c> y la tabla entera <c>SolicitudesConexionMicrosoft365</c>.
///
/// <para>
/// Un <c>SELECT</c> que pida lo retirado falla con 42501, así que nada que corra con este rol puede
/// materializar esas entidades. <c>ObtenerDocumentosGeneradosQuery</c> y
/// <c>ObtenerLoteGeneracionDocumentosQuery</c> proyectan. <c>DetectarCamposDocumentoQuery</c> no llega al
/// router de IA en Sesión Privilegiada, y las otras dos consultas que lo usan están en páginas que exigen un
/// rol de negocio, que esa sesión no lleva. El flujo OAuth de Microsoft 365 exige Administrador, así que la
/// tabla de solicitudes no se usa con este rol. <c>AuditoriaInterceptor</c> enmascara esas columnas en los
/// registros nuevos.
/// </para>
///
/// <para>
/// El <c>REVOKE … ON TABLE</c> retira también los privilegios de columna que hubiera. <c>DocumentosGenerados</c>
/// sigue sin <c>CodigoSeguroVerificacion</c>, y su <c>Down</c> vuelve a la lista de columnas anterior, sin ese
/// código; el resto de tablas tenía <c>SELECT</c> de tabla, que es lo que restaura el <c>Down</c>. No toca
/// <c>cae_app_runtime</c> ni <c>cae_app_aprovisionamiento</c>, ni RLS ni políticas.
/// <c>SoporteSinColumnasSecretasTests</c> congela que ninguna migración posterior lo vuelva a conceder.
/// </para>
/// </summary>
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
            REVOKE ALL ON TABLE public."ExtraccionesIaCache" FROM cae_app_soporte;
            GRANT SELECT ("Id", "HashSha256", "TipoEsperado", "VersionPipeline", "CreadaEnUtc", "TenantId")
                ON TABLE public."ExtraccionesIaCache" TO cae_app_soporte;
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
            REVOKE ALL ON TABLE public."ExtraccionesIaCache" FROM cae_app_soporte;
            GRANT SELECT ON TABLE public."ExtraccionesIaCache" TO cae_app_soporte;
            GRANT SELECT ON TABLE public."SolicitudesConexionMicrosoft365" TO cae_app_soporte;
            """);
    }
}
