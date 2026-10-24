using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// <see cref="FronteraEstadoDocumento"/> contra <see cref="CalculadoraEstadoDocumento"/>, día a día: la frontera nunca llega
/// tarde (ningún día anterior a ella cambia el estado) y, si hay frontera, el estado cambia en ella o en una frontera
/// posterior (no se inventa un cambio donde la calculadora no lo tiene más que con umbrales iguales).
/// </summary>
public class FronteraEstadoDocumentoTests
{
    private static readonly DateOnly Vencimiento = new(2026, 11, 30);

    public static TheoryData<int, int> Umbrales => new()
    {
        { 30, 7 },
        { 15, 15 },
        { 60, 0 },
        { 1, 1 },
    };

    [Theory]
    [MemberData(nameof(Umbrales))]
    public void Nunca_llega_tarde_y_el_estado_cambia_en_la_frontera(int ambar, int rojo)
    {
        var vigencia = VigenciaDocumento.VenceEl(Vencimiento);
        for (var hoy = Vencimiento.AddDays(-ambar - 10); hoy <= Vencimiento.AddDays(5); hoy = hoy.AddDays(1))
        {
            var estadoHoy = CalculadoraEstadoDocumento.Calcular(vigencia, hoy, ambar, rojo);
            var frontera = FronteraEstadoDocumento.ProximoCambio(vigencia, hoy, ambar, rojo);

            // Siguiente día en que la calculadora da otro estado (dentro de un horizonte holgado).
            DateOnly? primerCambioReal = null;
            for (var dia = hoy.AddDays(1); dia <= Vencimiento.AddDays(10); dia = dia.AddDays(1))
            {
                if (CalculadoraEstadoDocumento.Calcular(vigencia, dia, ambar, rojo) != estadoHoy)
                {
                    primerCambioReal = dia;
                    break;
                }
            }

            if (primerCambioReal is { } cambio)
            {
                frontera.Should().NotBeNull($"el {hoy} el estado {estadoHoy} cambia el {cambio}");
                frontera!.Value.Should().BeOnOrBefore(cambio, $"el {hoy} la frontera no puede llegar tarde");
            }
            else
            {
                frontera.Should().BeNull($"el {hoy} el estado {estadoHoy} ya no cambia por la fecha");
            }

            frontera?.Should().BeAfter(hoy);
        }
    }

    [Fact]
    public void Un_documento_que_no_caduca_o_sin_fecha_no_tiene_frontera()
    {
        var hoy = new DateOnly(2026, 10, 24);
        FronteraEstadoDocumento.ProximoCambio(VigenciaDocumento.NoCaduca, hoy, 30, 7).Should().BeNull();
        FronteraEstadoDocumento.ProximoCambio(VigenciaDocumento.SinConfirmar, hoy, 30, 7).Should().BeNull();
    }

    [Fact]
    public void Un_documento_ya_vencido_no_tiene_frontera()
    {
        FronteraEstadoDocumento.ProximoCambio(VigenciaDocumento.VenceEl(Vencimiento), Vencimiento.AddDays(1), 30, 7)
            .Should().BeNull();
    }

    [Fact]
    public void Minima_y_primera_posterior()
    {
        var hoy = new DateOnly(2026, 10, 24);
        FronteraEstadoDocumento.Minima(null, null).Should().BeNull();
        FronteraEstadoDocumento.Minima(hoy, null).Should().Be(hoy);
        FronteraEstadoDocumento.Minima(hoy.AddDays(3), hoy.AddDays(1)).Should().Be(hoy.AddDays(1));
        FronteraEstadoDocumento.PrimeraPosterior(hoy, hoy, hoy.AddDays(-1), null).Should().BeNull("hoy no es posterior a hoy");
        FronteraEstadoDocumento.PrimeraPosterior(hoy, hoy.AddDays(5), hoy.AddDays(2)).Should().Be(hoy.AddDays(2));
    }
    [Fact]
    public void Un_vencimiento_centinela_no_lanza_y_no_es_frontera()
    {
        var hoy = new DateOnly(2026, 10, 24);

        FronteraEstadoDocumento.DiaSiguiente(DateOnly.MaxValue).Should().BeNull("el día siguiente a 9999-12-31 no existe");
        FronteraEstadoDocumento.DiaSiguiente(null).Should().BeNull();
        FronteraEstadoDocumento.DiaSiguiente(hoy).Should().Be(hoy.AddDays(1));

        // 9999-12-31 entra en ámbar y en rojo, pero nunca vence: la frontera es la del umbral, sin lanzar al sumar un día.
        FronteraEstadoDocumento.ProximoCambio(VigenciaDocumento.VenceEl(DateOnly.MaxValue), hoy, 30, 7)
            .Should().Be(DateOnly.MaxValue.AddDays(-30));
        // Umbrales desmesurados (fuera del rango de fechas) tampoco lanzan.
        FronteraEstadoDocumento.ProximoCambio(VigenciaDocumento.VenceEl(DateOnly.MaxValue), hoy, int.MinValue + 1, int.MaxValue)
            .Should().BeNull();
    }
}