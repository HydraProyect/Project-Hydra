using CaeManager.Application.AltaAutonoma;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.AltaAutonoma;

/// <summary>
/// La orquestación de una pasada de limpieza de Solicitudes de alta (A8, DA-7): orden, lotes y topes. Los plazos y el
/// efecto sobre la base los mide la suite de integración sobre las funciones SQL.
/// </summary>
public class ServicioLimpiezaSolicitudesAltaTests
{
    private static ServicioLimpiezaSolicitudesAlta Servicio(
        ILimpiezaSolicitudesAlta limpieza, LimpiezaSolicitudesAltaOptions? opciones = null) =>
        new(limpieza, Options.Create(opciones ?? new LimpiezaSolicitudesAltaOptions()),
            NullLogger<ServicioLimpiezaSolicitudesAlta>.Instance);

    [Fact]
    public async Task Sin_nada_pendiente_hace_una_llamada_por_operacion_y_devuelve_ceros()
    {
        var limpieza = new LimpiezaFalsa(caducables: 0, borrables: 0);

        var resultado = await Servicio(limpieza).EjecutarAsync(CancellationToken.None);

        resultado.Should().Be(new ResultadoLimpiezaAlta(0, 0));
        limpieza.Llamadas.Should().Equal("caducar", "borrar");
    }

    [Fact]
    public async Task Caduca_antes_de_borrar_porque_el_borrado_solo_alcanza_lo_ya_caducado()
    {
        var limpieza = new LimpiezaFalsa(caducables: 3, borrables: 2);

        var resultado = await Servicio(limpieza).EjecutarAsync(CancellationToken.None);

        resultado.Should().Be(new ResultadoLimpiezaAlta(3, 2));
        limpieza.Llamadas.Should().Equal("caducar", "borrar");
    }

    [Fact]
    public async Task Encadena_lotes_hasta_que_uno_sale_incompleto()
    {
        var limpieza = new LimpiezaFalsa(caducables: 5, borrables: 4);
        var opciones = new LimpiezaSolicitudesAltaOptions { TamanoLote = 2 };

        var resultado = await Servicio(limpieza, opciones).EjecutarAsync(CancellationToken.None);

        resultado.Should().Be(new ResultadoLimpiezaAlta(5, 4));
        limpieza.Llamadas.Should().Equal("caducar", "caducar", "caducar", "borrar", "borrar", "borrar");
        limpieza.LotesPedidos.Should().AllBeEquivalentTo(2);
    }

    [Fact]
    public async Task Un_atasco_no_monopoliza_la_pasada_y_el_resto_queda_para_la_siguiente()
    {
        var limpieza = new LimpiezaFalsa(caducables: 1_000, borrables: 0);
        var opciones = new LimpiezaSolicitudesAltaOptions { TamanoLote = 10, MaximoLotesPorPasada = 3 };

        var resultado = await Servicio(limpieza, opciones).EjecutarAsync(CancellationToken.None);

        resultado.Caducadas.Should().Be(30);
        limpieza.Llamadas.Count(l => l == "caducar").Should().Be(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50_000, LimpiezaSolicitudesAltaOptions.TamanoLoteMaximo)]
    public async Task El_tamano_de_lote_se_acota_al_rango_que_admite_la_funcion(int configurado, int esperado)
    {
        var limpieza = new LimpiezaFalsa(caducables: 0, borrables: 0);

        await Servicio(limpieza, new LimpiezaSolicitudesAltaOptions { TamanoLote = configurado }).EjecutarAsync(CancellationToken.None);

        limpieza.LotesPedidos.Should().AllBeEquivalentTo(esperado);
    }

    [Fact]
    public async Task Cancelada_la_pasada_no_pide_otro_lote()
    {
        var limpieza = new LimpiezaFalsa(caducables: 100, borrables: 100);
        using var cancelacion = new CancellationTokenSource();
        cancelacion.Cancel();

        var pasada = () => Servicio(limpieza).EjecutarAsync(cancelacion.Token);

        await pasada.Should().ThrowAsync<OperationCanceledException>();
        limpieza.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public void Por_defecto_esta_activa_cada_hora_con_lotes_acotados()
    {
        var opciones = new LimpiezaSolicitudesAltaOptions();

        opciones.Activa.Should().BeTrue();
        opciones.Intervalo.Should().Be(TimeSpan.FromHours(1));
        opciones.TamanoLote.Should().BeInRange(1, LimpiezaSolicitudesAltaOptions.TamanoLoteMaximo);
        opciones.MaximoLotesPorPasada.Should().BePositive();
        LimpiezaSolicitudesAltaOptions.SeccionConfiguracion.Should().Be("AltaAutonoma:Limpieza");
    }

    private sealed class LimpiezaFalsa(int caducables, int borrables) : ILimpiezaSolicitudesAlta
    {
        private int _caducables = caducables;
        private int _borrables = borrables;

        public List<string> Llamadas { get; } = [];
        public List<int> LotesPedidos { get; } = [];

        public Task<int> CaducarVencidasAsync(int tamanoLote, CancellationToken cancellationToken)
        {
            Llamadas.Add("caducar");
            LotesPedidos.Add(tamanoLote);
            var hechas = Math.Min(tamanoLote, _caducables);
            _caducables -= hechas;
            return Task.FromResult(hechas);
        }

        public Task<int> BorrarDatosPersonalesAsync(int tamanoLote, CancellationToken cancellationToken)
        {
            Llamadas.Add("borrar");
            LotesPedidos.Add(tamanoLote);
            var hechas = Math.Min(tamanoLote, _borrables);
            _borrables -= hechas;
            return Task.FromResult(hechas);
        }
    }
}
