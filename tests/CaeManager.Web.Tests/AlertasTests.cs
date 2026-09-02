using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Alertas.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Mecaniza qué ámbitos puede ofrecer la reclamación agregada de /alertas
/// (DEC-4: por entidad, trabajador o empresa; DEC-11: primero el camino,
/// después la superficie). Trabajador y Empresa ya lo tienen completo
/// —dominio, agenda, lote y envío—; Cliente, Vehículo y Proyecto no, y
/// ObtenerLoteReclamacionPorFiltroQueryHandler sigue lanzando para ellos.
/// Este test rompe si alguien amplía <see cref="Alertas.AmbitosSoportados"/>
/// a uno de esos tres antes de construir su camino — justo el defecto A-08
/// (promesa navegable sin capacidad detrás).
/// </summary>
public class AlertasTests
{
    [Fact]
    public void Alertas_ofrece_Trabajador_y_Empresa()
    {
        Alertas.AmbitosSoportados.Should().BeEquivalentTo(
            [AmbitoAplicacion.Trabajador, AmbitoAplicacion.Empresa]);
    }

    [Theory]
    [InlineData(AmbitoAplicacion.Cliente)]
    [InlineData(AmbitoAplicacion.Vehiculo)]
    [InlineData(AmbitoAplicacion.Proyecto)]
    public void Alertas_no_ofrece_los_ambitos_sin_camino_de_reclamacion(AmbitoAplicacion ambito)
    {
        Alertas.AmbitosSoportados.Should().NotContain(ambito);
    }
}
