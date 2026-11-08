using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class AltaBuzonMicrosoft365ServiceTests
{
    private static readonly TokensGraphDto TokensDelegados = new("token-delegado", "refresh-delegado", DateTime.UtcNow.AddHours(1));

    private static (AltaBuzonMicrosoft365Service Servicio, Microsoft365GraphClientFalso Graph) Crear()
    {
        var graph = new Microsoft365GraphClientFalso { BuzonEmailPropio = "gestora@operador.test" };
        return (new AltaBuzonMicrosoft365Service(graph), graph);
    }

    [Fact]
    public async Task El_buzon_propio_se_abre_con_el_token_delegado_y_guarda_su_refresh_token()
    {
        var (servicio, graph) = Crear();

        var resultado = await servicio.PrepararAsync(TokensDelegados, buzonCompartido: null, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(new BuzonMicrosoft365Preparado(
            "gestora@operador.test", "token-delegado", "refresh-delegado", null, "inbox-propio"));
        graph.TokensDeIdentidad.Should().Equal("token-delegado");
        graph.DirectoriosConTokenDeAplicacion.Should().BeEmpty();
    }

    [Fact]
    public async Task Escribir_la_propia_direccion_en_el_campo_del_compartido_conecta_el_buzon_propio()
    {
        var (servicio, graph) = Crear();

        var resultado = await servicio.PrepararAsync(TokensDelegados, "GESTORA@operador.test", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.RefreshToken.Should().Be("refresh-delegado");
        resultado.Valor.DirectorioEntraId.Should().BeNull();
        graph.DirectoriosConTokenDeAplicacion.Should().BeEmpty();
        graph.BuzonesUsados.Should().OnlyContain(b => !b.EsCompartido);
    }

    [Fact]
    public async Task El_compartido_usa_el_directorio_de_la_sesion_y_el_token_de_aplicacion_y_no_guarda_el_refresh_token()
    {
        var (servicio, graph) = Crear();
        graph.DirectorioDevuelto = "directorio-de-la-sesion";
        graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-compartido";

        var resultado = await servicio.PrepararAsync(TokensDelegados, "cae@operador.test", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(new BuzonMicrosoft365Preparado(
            "cae@operador.test", "token-aplicacion-falso", null, "directorio-de-la-sesion", "inbox-compartido"));
        graph.DirectoriosConTokenDeAplicacion.Should().Equal("directorio-de-la-sesion");

        // La sonda de identidad del compartido va con el token de aplicación, nunca con el de la persona.
        graph.TokensDeIdentidad.Should().Equal("token-aplicacion-falso");
        graph.BuzonesUsados.Should().ContainSingle(b => b.EsCompartido && b.Direccion == "cae@operador.test");
    }

    [Fact]
    public async Task Un_compartido_fuera_del_ambito_de_Exchange_no_se_da_de_alta()
    {
        var (servicio, graph) = Crear();
        graph.ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "403");

        var resultado = await servicio.PrepararAsync(TokensDelegados, "ajeno@operador.test", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AltaBuzonMicrosoft365Service.ErrorSinAccesoCompartido);
    }

    [Fact]
    public async Task Un_fallo_transitorio_de_la_sonda_del_compartido_no_se_presenta_como_falta_de_acceso()
    {
        var (servicio, graph) = Crear();
        graph.ErrorIdentidadBuzon = Error.Crear("Integraciones.Microsoft365.TiempoAgotado", "timeout");

        var resultado = await servicio.PrepararAsync(TokensDelegados, "cae@operador.test", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AltaBuzonMicrosoft365Service.ErrorAutenticacion);
    }

    [Fact]
    public async Task Sin_token_de_aplicacion_no_hay_sonda_ni_alta()
    {
        var (servicio, graph) = Crear();
        graph.FallaTokenAplicacion = true;

        var resultado = await servicio.PrepararAsync(TokensDelegados, "cae@operador.test", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AltaBuzonMicrosoft365Service.ErrorSinAccesoCompartido);
        graph.TokensDeIdentidad.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_de_la_identidad_del_buzon_propio_no_da_de_alta()
    {
        var (servicio, graph) = Crear();
        graph.ErrorIdentidadBuzon = Error.Crear("Integraciones.Microsoft365.ErrorApi", "500");

        var resultado = await servicio.PrepararAsync(TokensDelegados, buzonCompartido: null, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AltaBuzonMicrosoft365Service.ErrorAutenticacion);
    }
}
