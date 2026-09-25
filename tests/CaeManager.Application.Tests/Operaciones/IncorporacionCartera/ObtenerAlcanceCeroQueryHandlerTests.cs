using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tests.Clientes;
using FluentAssertions;

namespace CaeManager.Application.Tests.Operaciones.IncorporacionCartera;

/// <summary>
/// P0-9a: la Query que usan /bandeja, /alertas y las listas para no pintar un
/// vacío positivo con alcance cero aplica el mismo criterio que Mi trabajo
/// (#859): sin acceso total y con la cartera de Clientes empresariales vacía.
/// </summary>
public class ObtenerAlcanceCeroQueryHandlerTests
{
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    public async Task Alcance_cero_solo_sin_acceso_total_y_con_la_cartera_vacia(bool accesoTotal, int clientes, bool esperado)
    {
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: accesoTotal,
            clienteIdsVisibles: Enumerable.Range(0, clientes).Select(_ => Guid.NewGuid()).ToList());

        var resultado = await new ObtenerAlcanceCeroQueryHandler(alcance).Handle(new ObtenerAlcanceCeroQuery(), CancellationToken.None);

        resultado.Should().Be(esperado);
    }
}
