using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class CredencialIntegracionTests
{
    [Fact]
    public void Se_crea_con_el_refresh_token_informado()
    {
        var credencial = new CredencialIntegracion(Guid.NewGuid(), "token-inicial");

        credencial.RefreshToken.Should().Be("token-inicial");
    }

    [Fact]
    public void Rechaza_una_conexion_vacia()
    {
        var accion = () => new CredencialIntegracion(Guid.Empty, "token");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_refresh_token_vacio()
    {
        var accion = () => new CredencialIntegracion(Guid.NewGuid(), " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActualizarRefreshToken_reemplaza_el_token_entero()
    {
        var credencial = new CredencialIntegracion(Guid.NewGuid(), "token-viejo");

        credencial.ActualizarRefreshToken("token-nuevo");

        credencial.RefreshToken.Should().Be("token-nuevo");
    }

    /// <summary>Auditoría módulo 6: caché del access token — sin ella, AccesoGraphService rotaba el refresh token en cada operación aunque el access token anterior siguiera sirviendo.</summary>
    public class CacheDeAccessToken
    {
        private static readonly DateTime Ahora = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Sin_access_token_cacheado_no_esta_vigente()
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), "refresh-token");

            credencial.TieneAccessTokenVigente(Ahora).Should().BeFalse();
        }

        [Fact]
        public void Con_un_access_token_reciente_esta_vigente()
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), "refresh-token");
            credencial.ActualizarAccessTokenCacheado("access-token", Ahora.AddHours(1));

            credencial.TieneAccessTokenVigente(Ahora).Should().BeTrue();
        }

        [Fact]
        public void Un_access_token_ya_expirado_no_esta_vigente()
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), "refresh-token");
            credencial.ActualizarAccessTokenCacheado("access-token", Ahora.AddMinutes(-1));

            credencial.TieneAccessTokenVigente(Ahora).Should().BeFalse();
        }

        /// <summary>Margen de seguridad: no se considera vigente si expira dentro de los próximos 2 minutos, para no arriesgar que caduque a mitad de la llamada que lo usa.</summary>
        [Fact]
        public void Un_access_token_a_punto_de_expirar_no_se_considera_vigente()
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), "refresh-token");
            credencial.ActualizarAccessTokenCacheado("access-token", Ahora.AddMinutes(1));

            credencial.TieneAccessTokenVigente(Ahora).Should().BeFalse();
        }

        [Fact]
        public void Rechaza_un_access_token_vacio()
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), "refresh-token");

            var accion = () => credencial.ActualizarAccessTokenCacheado(" ", Ahora.AddHours(1));

            accion.Should().Throw<ArgumentException>();
        }
    }
}
