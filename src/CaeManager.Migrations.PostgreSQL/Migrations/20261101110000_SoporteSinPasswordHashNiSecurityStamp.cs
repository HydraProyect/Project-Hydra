using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Soporte TALVEG deja de leer <c>AspNetUsers.PasswordHash</c> y <c>AspNetUsers.SecurityStamp</c> (OWASP ASVS 4.0.3
    /// V4.1.3, mínimo privilegio): el <c>SELECT</c> de tabla de <c>cae_app_soporte</c> se sustituye por una concesión de
    /// columnas con todas las demás. La RLS de <c>AspNetUsers</c> no cambia.
    ///
    /// <para>
    /// Bajo Sesión Privilegiada ya nada materializa la cuenta entera: las lecturas proyectan, y las rutas que identifican
    /// a una persona corren sin la selección (<c>SeleccionDescartadaEnIdentificacionMiddleware</c>). La vigencia de la
    /// cuenta firmada la responde <c>app_cuenta_firmada_vigente</c>, y <c>app_conceder_administrador_por_plataforma</c>
    /// consulta <c>PasswordHash</c> como propietaria: las dos son <c>SECURITY DEFINER</c> y no dependen del privilegio que
    /// aquí se retira.
    /// </para>
    ///
    /// <para>
    /// Una columna nueva de <c>AspNetUsers</c> no queda concedida a <c>cae_app_soporte</c>: hay que clasificarla y
    /// concederla de forma expresa. El privilegio se comprueba al ejecutar cada consulta, de modo que la versión anterior
    /// de la aplicación que siga en marcha durante el relevo recibe <c>42501</c> si materializa una cuenta bajo Sesión
    /// Privilegiada: falla cerrada, y solo hasta el relevo.
    /// </para>
    /// </summary>
    public partial class SoporteSinPasswordHashNiSecurityStamp : Migration
    {
        private const string ColumnasLegibles = """
            "Id", "AccessFailedCount", "Avatar", "ClavesApiValidasDesdeUtc", "ClienteId", "ConcurrencyStamp",
            "CoordinadorUsuarioId", "DebeCambiarContrasena", "Email", "EmailConfirmed", "FechaBajaUtc", "FechaCreacion",
            "Idioma", "LockoutEnabled", "LockoutEnd", "NombreCompleto", "NormalizedEmail", "NormalizedUserName",
            "PermisoConsultarAccesoDocumentosSensibles", "PhoneNumber", "PhoneNumberConfirmed", "Tema", "TenantId",
            "TwoFactorEnabled", "UltimaActividadUtc", "UserName"
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                REVOKE SELECT ON TABLE public."AspNetUsers" FROM cae_app_soporte;
                GRANT SELECT ({ColumnasLegibles}) ON TABLE public."AspNetUsers" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                REVOKE SELECT ({ColumnasLegibles}) ON TABLE public."AspNetUsers" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."AspNetUsers" TO cae_app_soporte;
                """);
        }
    }
}
