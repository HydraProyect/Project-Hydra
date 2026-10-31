using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG no lee las columnas secretas que quedaban (OWASP ASVS 4.0.3 V4.1.3, mínimo
    /// privilegio, y V2.10.4, secretos fuera del alcance de quien no los usa). Sigue a
    /// <c>SoporteSinLecturaDeCredenciales</c>: la línea base concedía a <c>cae_app_soporte</c> —el rol que
    /// <c>TenantRlsConnectionInterceptor</c> adopta en cuanto hay una Sesión Privilegiada seleccionada—
    /// <c>SELECT</c> sobre la tabla entera de seis tablas con un secreto en una columna. A cada una se le
    /// retira el grant de tabla y se le concede <c>SELECT</c> por columna de todas las demás:
    /// <list type="bullet">
    /// <item><c>CanalesGestionDocumental</c>, <c>CredencialesAccesoEmpresa</c> y
    /// <c>CredencialesAccesoSubcontrata</c>: <c>Contrasena</c>, la contraseña de la plataforma CAE o del
    /// portal externo. Solo la leen las consultas marcadas con <c>IConsultaDeSecretosDeTenant</c>, que
    /// <c>AutorizacionSecretosDeTenantBehavior</c> ya rechaza en Sesión Privilegiada; las demás lecturas
    /// proyectan.</item>
    /// <item><c>SuscripcionesWebhook</c>: <c>ClientState</c>, el secreto compartido con el que se validan las
    /// notificaciones de Microsoft Graph. Solo lo leen la recepción del webhook y la renovación de
    /// suscripciones, que no corren en Sesión Privilegiada.</item>
    /// <item><c>LineasWhatsApp</c>: <c>TokenAcceso</c>, el token de la API de WhatsApp Business. Solo lo
    /// leen el envío y la ingesta de mensajes; el listado de líneas proyecta sin él.</item>
    /// <item><c>ConexionesIntegracion</c>: <c>UltimoError</c>, el texto que devuelve el proveedor al fallar,
    /// que puede llevar detalles de la cuenta o del token. Los listados de conexiones y de líneas lo leen
    /// aparte y solo fuera de Sesión Privilegiada.</item>
    /// </list>
    ///
    /// <para>
    /// <c>AccesosPlataformaCaeEmpresa</c> no aparece porque <c>AnadePlataformaCaeDeClienteEmpresarial</c> ya
    /// le retiró todo a <c>cae_app_soporte</c>. <c>AspNetUsers</c> conserva su grant de tabla, con
    /// <c>PasswordHash</c> y <c>SecurityStamp</c>: <c>UserManager</c> materializa la cuenta entera, y lo hace
    /// en Sesión Privilegiada en cada página (la guarda de <c>MainLayout</c>, <c>CurrentUserService</c>) y al
    /// revalidar el sello de seguridad.
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
    public partial class SoporteSinColumnasSecretasRestantes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."CanalesGestionDocumental" FROM cae_app_soporte;
                GRANT SELECT ("Id", "CentroId", "CreadoEnUtc", "EliminadoEnUtc", "EliminadoPorUsuarioId",
                              "EmailsDestinatarios", "EsPrincipal", "EstaEliminado", "EtiquetaProposito",
                              "NombreContacto", "Notas", "ProveedorPlataformaCaeId", "TenantId", "Tipo",
                              "UrlAcceso", "Usuario", "Version")
                    ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;

                REVOKE ALL ON TABLE public."CredencialesAccesoEmpresa" FROM cae_app_soporte;
                GRANT SELECT ("Id", "CampoEmpresa", "EmpresaId", "Notas", "TenantId", "UrlAcceso", "Usuario")
                    ON TABLE public."CredencialesAccesoEmpresa" TO cae_app_soporte;

                REVOKE ALL ON TABLE public."CredencialesAccesoSubcontrata" FROM cae_app_soporte;
                GRANT SELECT ("Id", "CampoEmpresa", "Notas", "SubcontrataId", "TenantId", "UrlAcceso", "Usuario")
                    ON TABLE public."CredencialesAccesoSubcontrata" TO cae_app_soporte;

                REVOKE ALL ON TABLE public."SuscripcionesWebhook" FROM cae_app_soporte;
                GRANT SELECT ("Id", "ConexionIntegracionId", "FechaExpiracionUtc", "GraphSubscriptionId", "TenantId")
                    ON TABLE public."SuscripcionesWebhook" TO cae_app_soporte;

                REVOKE ALL ON TABLE public."LineasWhatsApp" FROM cae_app_soporte;
                GRANT SELECT ("Id", "ComercialAsignadoId", "ConexionIntegracionId", "MensajeAutoTriage", "Modo",
                              "NumeroTelefono", "PhoneNumberId", "TenantId", "WabaId")
                    ON TABLE public."LineasWhatsApp" TO cae_app_soporte;

                REVOKE ALL ON TABLE public."ConexionesIntegracion" FROM cae_app_soporte;
                GRANT SELECT ("Id", "BuzonEmail", "ClienteId", "CreadoEnUtc", "EliminadoEnUtc", "EliminadoPorUsuarioId",
                              "EstaEliminado", "Estado", "FechaConectadaUtc", "GestorPropietarioId", "Nombre",
                              "Proveedor", "TenantId", "Version")
                    ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."ConexionesIntegracion" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."LineasWhatsApp" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."LineasWhatsApp" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."SuscripcionesWebhook" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."SuscripcionesWebhook" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."CredencialesAccesoSubcontrata" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."CredencialesAccesoSubcontrata" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."CredencialesAccesoEmpresa" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."CredencialesAccesoEmpresa" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."CanalesGestionDocumental" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;
                """);
        }
    }
}
