using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG no lee credenciales (OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio, y V2.10.4, secretos
    /// fuera del alcance de quien no los usa). La línea base concedía a <c>cae_app_soporte</c> —el rol que
    /// <c>TenantRlsConnectionInterceptor</c> adopta en cuanto hay una Sesión Privilegiada seleccionada—
    /// <c>SELECT</c> sobre la tabla entera de tres almacenes de secretos:
    /// <list type="bullet">
    /// <item><c>AspNetUserTokens</c>: la clave TOTP (<c>AuthenticatorKey</c>), los hashes de los códigos de
    /// recuperación y el último paso TOTP aceptado de <b>todas</b> las cuentas. La tabla no tiene RLS, así que
    /// el grant alcanzaba a cualquier Tenant. Se retira entero: ninguna lectura de Soporte necesita una columna
    /// suya. El restablecimiento de la 2FA por Soporte escribe con la función <c>SECURITY DEFINER</c>
    /// <c>app_restablecer_segundo_factor_por_soporte</c>, que no depende de este grant.</item>
    /// <item><c>CredencialesIntegracion</c>: el refresh token y el access token de Microsoft 365 de cada
    /// conexión. Se retira entero: solo los lee <c>AccesoGraphService</c>, que materializa la credencial
    /// completa y no tiene sentido sin los tokens. Bajo una Sesión Privilegiada la vista en vivo del buzón deja
    /// de cargar (la página ya lo trata como «no pudimos leer las carpetas»).</item>
    /// <item><c>ClavesApi</c>: <c>HashClave</c> es el SHA-256 de la clave. Se retira el grant de tabla y se
    /// concede <c>SELECT</c> por columna de todas las demás, que es lo que necesita
    /// <c>ObtenerClavesApiQuery</c> (y el filtro de Tenant y de borrado lógico).</item>
    /// </list>
    ///
    /// <para>
    /// <c>ConexionesIntegracion</c> conserva su grant de tabla: sus columnas (buzón, nombre, estado, último error,
    /// proveedor, Gestor CAE propietario) no guardan ningún secreto, y las leen consultas que se ejecutan en
    /// Sesión Privilegiada (conexiones, líneas de WhatsApp, conversaciones).
    /// </para>
    ///
    /// <para>
    /// <c>REVOKE … ON TABLE</c> retira también los privilegios de columna que hubiera, así que el estado final
    /// no depende de lo que hubiera antes. No toca <c>cae_app_runtime</c> ni <c>cae_app_aprovisionamiento</c>,
    /// ni RLS ni políticas, ni el <c>ALTER DEFAULT PRIVILEGES</c> de la línea base, que solo afecta a tablas
    /// nuevas. <c>SoporteSinColumnasSecretasTests</c> congela que ninguna migración posterior vuelva a conceder
    /// estas columnas a <c>cae_app_soporte</c>.
    /// </para>
    /// </summary>
    public partial class SoporteSinLecturaDeCredenciales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."AspNetUserTokens" FROM cae_app_soporte;
                REVOKE ALL ON TABLE public."CredencialesIntegracion" FROM cae_app_soporte;

                REVOKE ALL ON TABLE public."ClavesApi" FROM cae_app_soporte;
                GRANT SELECT ("Id", "NombreDescriptivo", "PrefijoVisible", "CreadaPorUsuarioId", "UltimoUsoUtc",
                              "RevocadaEnUtc", "CaducaEnUtc", "TenantId", "Version", "CreadoEnUtc",
                              "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId")
                    ON TABLE public."ClavesApi" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."ClavesApi" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."ClavesApi" TO cae_app_soporte;
                GRANT SELECT ON TABLE public."CredencialesIntegracion" TO cae_app_soporte;
                GRANT SELECT ON TABLE public."AspNetUserTokens" TO cae_app_soporte;
                """);
        }
    }
}
