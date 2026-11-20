using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// El badge de Visitas de <c>/centros</c> y de Centro 360 solo lee las Visitas de los Centros visibles
/// (hallazgo de la segunda opinión sobre la PR #2379). Los Ids que llegan los pinta la página, no son autoridad: un Tenant
/// solo Inbound con el papel de Tenant de origen, dentro de la petición de Outbound, recibe del alcance una lista vacía y
/// la respuesta sale vacía aunque haya Visitas. Con el alcance real lo prueba <c>AlcanceConPapelInboundTests</c> (Web);
/// aquí, el cruce del handler con la lista que devuelve el alcance.
/// </summary>
public class ObtenerProximaVisitaPorCentroAlcanceTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private readonly Guid _centroA = Guid.NewGuid();
    private readonly Guid _centroB = Guid.NewGuid();
    private readonly List<Visita> _visitas = [];

    public ObtenerProximaVisitaPorCentroAlcanceTests()
    {
        _visitas.Add(new Visita(_centroA, Hoy.AddDays(1), Hoy.AddDays(2), notas: null));
        _visitas.Add(new Visita(_centroB, Hoy.AddDays(3), Hoy.AddDays(4), notas: null));
    }

    private Task<IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>> Consultar(IReadOnlyList<Guid>? centrosVisibles) =>
        new ObtenerProximaVisitaPorCentroQueryHandler(new ContextoFalso(_visitas),
                new AlcanceDatosServiceFalso(tieneAccesoTotal: centrosVisibles is null, centroIdsVisibles: centrosVisibles))
            .Handle(new ObtenerProximaVisitaPorCentroQuery([_centroA, _centroB]), CancellationToken.None);

    [Fact]
    public async Task Sin_Centros_visibles_la_respuesta_sale_vacia_aunque_haya_Visitas()
    {
        (await Consultar([])).Should().BeEmpty(
            "el Tenant solo Inbound con el papel de Tenant de origen no tiene alcance de Outbound: ningún Centro visible");
    }

    [Fact]
    public async Task Sin_restriccion_se_ven_las_Visitas_de_todos_los_Centros_pedidos()
    {
        (await Consultar(null)).Keys.Should().BeEquivalentTo([_centroA, _centroB],
            "un Tenant de origen con Outbound sigue viendo sus Visitas");
    }

    [Fact]
    public async Task Con_una_lista_solo_se_ven_los_Centros_de_la_lista()
    {
        (await Consultar([_centroA])).Keys.Should().BeEquivalentTo([_centroA]);
    }

    private sealed class ContextoFalso(List<Visita> visitas) : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(visitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores =>
            new TestAsyncQueryable<VisitaTrabajador>(new List<VisitaTrabajador>().AsQueryable());
    }
}
