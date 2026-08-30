using CaeManager.Application.Integraciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Auditoría módulo 6: antes, cada llamada refrescaba contra Graph sin
/// comprobar si el access token vigente todavía servía — cuantas más veces
/// se rota el refresh token, más ventanas hay para que dos operaciones
/// concurrentes sobre la misma conexión compitan por el mismo refresco (ver
/// CredencialIntegracionTests.CacheDeAccessToken y el token de concurrencia
/// de CredencialIntegracion.Version).
/// </summary>
public class AccesoGraphServiceTests
{
    [Fact]
    public async Task Sin_access_token_cacheado_refresca_contra_graph()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var conexionId = Guid.NewGuid();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexionId, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso();
        var servicio = new AccesoGraphService(credencialRepositorio, graphClient);

        var resultado = await servicio.ObtenerAccessTokenVigenteAsync(conexionId, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(graphClient.AccessTokenDevuelto);
        graphClient.VecesRefrescado.Should().Be(1);
    }

    [Fact]
    public async Task Con_access_token_cacheado_y_vigente_no_vuelve_a_refrescar()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var conexionId = Guid.NewGuid();
        var credencial = new CredencialIntegracion(conexionId, "refresh-token");
        credencial.ActualizarAccessTokenCacheado("access-token-cacheado", DateTime.UtcNow.AddMinutes(30));
        credencialRepositorio.Agregar(credencial);
        var graphClient = new Microsoft365GraphClientFalso();
        var servicio = new AccesoGraphService(credencialRepositorio, graphClient);

        var resultado = await servicio.ObtenerAccessTokenVigenteAsync(conexionId, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("access-token-cacheado");
        graphClient.VecesRefrescado.Should().Be(0, "el access token vigente no debe gastar un refresco real contra Graph");
    }

    [Fact]
    public async Task Con_access_token_cacheado_pero_expirado_vuelve_a_refrescar()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var conexionId = Guid.NewGuid();
        var credencial = new CredencialIntegracion(conexionId, "refresh-token");
        credencial.ActualizarAccessTokenCacheado("access-token-viejo", DateTime.UtcNow.AddMinutes(-5));
        credencialRepositorio.Agregar(credencial);
        var graphClient = new Microsoft365GraphClientFalso();
        var servicio = new AccesoGraphService(credencialRepositorio, graphClient);

        var resultado = await servicio.ObtenerAccessTokenVigenteAsync(conexionId, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(graphClient.AccessTokenDevuelto);
        graphClient.VecesRefrescado.Should().Be(1);
    }

    [Fact]
    public async Task Al_refrescar_actualiza_la_cache_del_access_token_y_el_refresh_token()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var conexionId = Guid.NewGuid();
        var credencial = new CredencialIntegracion(conexionId, "refresh-token-viejo");
        credencialRepositorio.Agregar(credencial);
        var graphClient = new Microsoft365GraphClientFalso { RefreshTokenDevuelto = "refresh-token-rotado" };
        var servicio = new AccesoGraphService(credencialRepositorio, graphClient);

        await servicio.ObtenerAccessTokenVigenteAsync(conexionId, CancellationToken.None);

        credencial.RefreshToken.Should().Be("refresh-token-rotado");
        credencial.TieneAccessTokenVigente(DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task Sin_credencial_devuelve_fallo()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso();
        var servicio = new AccesoGraphService(credencialRepositorio, graphClient);

        var resultado = await servicio.ObtenerAccessTokenVigenteAsync(Guid.NewGuid(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.SinCredencial");
    }
}
