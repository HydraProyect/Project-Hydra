using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG no lee el código seguro de verificación de los documentos generados (OWASP ASVS 4.0.3
    /// V4.1.3, mínimo privilegio). Sigue a <c>SoporteSinColumnasSecretasRestantes</c>: la línea base concedía a
    /// <c>cae_app_soporte</c> —el rol que <c>TenantRlsConnectionInterceptor</c> adopta en cuanto hay una Sesión
    /// Privilegiada seleccionada— <c>SELECT</c> sobre la tabla entera de <c>DocumentosGenerados</c>. Se retira
    /// el grant de tabla y se concede <c>SELECT</c> por columna de todas las demás.
    ///
    /// <para>
    /// <c>CodigoSeguroVerificacion</c> está hoy siempre vacía: ninguna generación la rellena hasta que exista
    /// una plantilla de origen <c>TalvegPropio</c>. La revocación se adelanta a ese momento y no cambia lo que
    /// ve nadie en Sesión Privilegiada.
    /// </para>
    ///
    /// <para>
    /// Un <c>SELECT</c> que pida la columna falla con 42501, así que nada que corra con este rol puede
    /// materializar <c>DocumentoGenerado</c> entera. <c>ObtenerDocumentosGeneradosQuery</c> proyecta y
    /// <c>ObtenerTotalDocumentosGeneradosConAvisosQuery</c> solo cuenta. La exportación completa de un Tenant y
    /// la retirada de un Tenant de demostración sí la materializan, pero son modos de CLI, sin Sesión
    /// Privilegiada. <c>DocumentoGeneradoRepository.ObtenerPorIdAsync</c> también lo haría, y hoy no tiene
    /// llamadores. <c>AuditoriaInterceptor</c> enmascara el código: sin eso, Soporte TALVEG lo leería en
    /// <c>RegistrosAuditoria</c>.
    /// </para>
    ///
    /// <para>
    /// <c>VerificacionesDocumentoOficial.CodigoVerificacion</c> no aparece: la consulta del detalle de documento
    /// materializa esa entidad y dos vistas muestran el código, de modo que retirarlo rompería esas pantallas
    /// en Sesión Privilegiada. Queda como deuda de un incremento propio.
    /// </para>
    ///
    /// <para>
    /// <c>REVOKE … ON TABLE</c> retira también los privilegios de columna que hubiera, así que el estado final
    /// no depende de lo que hubiera antes. La línea base solo concedía <c>SELECT</c>, que es lo que restaura el
    /// <c>Down</c>. No toca <c>cae_app_runtime</c> ni <c>cae_app_aprovisionamiento</c>, ni RLS ni políticas, ni el
    /// <c>ALTER DEFAULT PRIVILEGES</c> de la línea base, que solo afecta a tablas nuevas.
    /// <c>SoporteSinColumnasSecretasTests</c> congela que ninguna migración posterior vuelva a conceder la
    /// columna a <c>cae_app_soporte</c>.
    /// </para>
    /// </summary>
    public partial class SoporteSinCodigoSeguroDocumentosGenerados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."DocumentosGenerados" FROM cae_app_soporte;
                GRANT SELECT ("Id", "CentroId", "CreadoEnUtc", "DatosUtilizadosJson", "DocumentoId",
                              "EliminadoEnUtc", "EliminadoPorUsuarioId", "EmpresaId", "EstaEliminado",
                              "Estado", "GeneradoEnUtc", "GeneradoPorUsuarioId", "HashSha256Impreso",
                              "PlantillaDocumentoVersionId", "SelloElectronicoAplicadoEnUtc", "TenantId",
                              "TrabajadorId", "Version")
                    ON TABLE public."DocumentosGenerados" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."DocumentosGenerados" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."DocumentosGenerados" TO cae_app_soporte;
                """);
        }
    }
}
