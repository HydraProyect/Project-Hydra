using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG no lee el identificador de sesión de usuario (OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio).
    /// Sigue a <c>SoporteSinCodigoSeguroDocumentosGenerados</c>: <c>AnadeSesionesUsuario</c> concedió a
    /// <c>cae_app_soporte</c> —el rol que <c>TenantRlsConnectionInterceptor</c> adopta en cuanto hay una Sesión
    /// Privilegiada seleccionada— <c>SELECT</c> sobre la tabla entera de <c>SesionesUsuario</c>. Se retira el
    /// grant de tabla y se concede <c>SELECT</c> por columna de todas las demás.
    ///
    /// <para>
    /// Un <c>SELECT</c> que pida la columna falla con 42501, así que nada que corra con este rol puede
    /// materializar <c>SesionUsuario</c> entera ni filtrar por <c>Sid</c>. La vigencia de la sesión, que se
    /// pregunta en cada revalidación de la cookie y del circuito también durante una Sesión Privilegiada, va por
    /// <c>app_sesion_vigente</c>: es SECURITY DEFINER y este rol conserva su EXECUTE, de modo que no depende de
    /// estos privilegios de columna. <c>RegistroSesionesUsuario.RevocarAsync</c> sí filtra por <c>Sid</c>, pero
    /// es una escritura que este rol nunca tuvo, así que su resultado bajo él no cambia; por eso
    /// <c>CierreSesionEnServidor</c> la lanza en un ámbito con la selección descartada. La exportación completa
    /// de un Tenant y la retirada de un Tenant de demostración materializan la entidad, pero son modos de CLI,
    /// sin Sesión Privilegiada.
    /// </para>
    ///
    /// <para>
    /// <c>AuditoriaInterceptor</c> no enmascara la columna porque excluye la entidad entera: ninguna fila de
    /// <c>RegistrosAuditoria</c> lleva el identificador desde que la tabla existe.
    /// </para>
    ///
    /// <para>
    /// <c>REVOKE … ON TABLE</c> retira también los privilegios de columna que hubiera, así que el estado final
    /// no depende de lo que hubiera antes. <c>AnadeSesionesUsuario</c> solo concedía <c>SELECT</c>, que es lo
    /// que restaura el <c>Down</c>. No toca <c>cae_app_runtime</c> ni <c>cae_app_aprovisionamiento</c>, ni RLS ni
    /// políticas, ni el <c>ALTER DEFAULT PRIVILEGES</c> de la línea base, que solo afecta a tablas nuevas.
    /// <c>SoporteSinColumnasSecretasTests</c> congela que ninguna migración posterior vuelva a conceder la
    /// columna a <c>cae_app_soporte</c>.
    /// </para>
    /// </summary>
    public partial class SoporteSinSidSesionUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."SesionesUsuario" FROM cae_app_soporte;
                GRANT SELECT ("Id", "UsuarioId", "EmitidaEnUtc", "RevocadaEnUtc", "TenantId")
                    ON TABLE public."SesionesUsuario" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."SesionesUsuario" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."SesionesUsuario" TO cae_app_soporte;
                """);
        }
    }
}
