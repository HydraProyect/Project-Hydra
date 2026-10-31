using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Soporte TALVEG deja de leer el código de verificación de un documento oficial y el contenido crudo de un
/// webhook (OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio). Sigue a <c>SoporteSinJsonSensibles</c>:
/// <c>cae_app_soporte</c> pierde <c>VerificacionesDocumentoOficial.CodigoVerificacion</c> y
/// <c>EventosWebhook.PayloadCrudo</c>, y conserva por columnas todas las demás de esas dos tablas.
///
/// <para>
/// Un <c>SELECT</c> que pida lo retirado falla con 42501, así que nada que corra con este rol puede materializar
/// esas entidades. <c>ObtenerValidacionOficialDocumentoQuery</c> proyecta sin la columna en Sesión Privilegiada,
/// y las dos pantallas dicen «No disponible para Soporte TALVEG», distinto de un documento sin código. Las
/// lecturas de <c>EventoWebhook</c> y <c>VerificacionDocumentoOficial</c> que materializan la entidad son de
/// servicios en segundo plano, sin petición ni Sesión Privilegiada, y corren con su rol de siempre.
/// <c>AuditoriaInterceptor</c> enmascara ambas columnas en los registros nuevos; las copias que ya estén en
/// <c>RegistrosAuditoria</c> no se tocan.
/// </para>
///
/// <para>
/// El <c>REVOKE … ON TABLE</c> retira también los privilegios de columna que hubiera; las dos tablas tenían
/// <c>SELECT</c> de tabla, que es lo que restaura el <c>Down</c>. No toca <c>cae_app_runtime</c> ni
/// <c>cae_app_aprovisionamiento</c>, ni RLS ni políticas. <c>SoporteSinColumnasSecretasTests</c> congela que
/// ninguna migración posterior lo vuelva a conceder.
/// </para>
/// </summary>
public partial class SoporteSinCodigoVerificacionNiPayloadWebhook : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE public."VerificacionesDocumentoOficial" FROM cae_app_soporte;
            GRANT SELECT ("Id", "DocumentoId", "Perfil", "NivelConfianza", "CifDetectado",
                          "RazonSocialDetectada", "FechaEmisionDetectada", "PeriodoDetectado",
                          "ResultadoCotejo", "Decision", "Motivos", "CreadaEnUtc", "TenantId")
                ON TABLE public."VerificacionesDocumentoOficial" TO cae_app_soporte;
            REVOKE ALL ON TABLE public."EventosWebhook" FROM cae_app_soporte;
            GRANT SELECT ("Id", "ConexionIntegracionId", "Intentos", "ErrorProcesado", "FechaRecepcionUtc",
                          "TenantId", "Estado", "IniciadoEnUtc", "SiguienteIntentoEnUtc", "PayloadRedactado")
                ON TABLE public."EventosWebhook" TO cae_app_soporte;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE public."VerificacionesDocumentoOficial" FROM cae_app_soporte;
            GRANT SELECT ON TABLE public."VerificacionesDocumentoOficial" TO cae_app_soporte;
            REVOKE ALL ON TABLE public."EventosWebhook" FROM cae_app_soporte;
            GRANT SELECT ON TABLE public."EventosWebhook" TO cae_app_soporte;
            """);
    }
}
