using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasAnterioresDeCentro;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

public class ObtenerVisitasAnterioresDeCentroQueryTests
{
    private sealed class VisitasFalsas : IVisitasQueryContext
    {
        public List<Visita> Lista { get; } = [];
        public List<VisitaTrabajador> Participaciones { get; } = [];
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Lista.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(Participaciones.AsQueryable());
    }

    [Fact]
    public async Task Filtra_el_centro_y_separa_pasadas_y_canceladas_de_las_activas()
    {
        var centro = Guid.NewGuid();
        var hoy = DiaDeNegocio.Hoy();
        var contexto = new VisitasFalsas();
        var antigua = new Visita(centro, hoy.AddDays(-8), hoy.AddDays(-6), null);
        var reciente = new Visita(centro, hoy.AddDays(-3), hoy.AddDays(-1), null);
        reciente.MarcarDocumentacionGestionada(DateTime.UtcNow);
        var cancelada = new Visita(centro, hoy.AddDays(2), hoy.AddDays(3), null);
        cancelada.Cancelar(DateTime.UtcNow, null);
        contexto.Lista.AddRange([antigua, reciente, cancelada,
            new Visita(centro, hoy.AddDays(-1), hoy, null),
            new Visita(centro, hoy.AddDays(1), hoy.AddDays(2), null),
            new Visita(Guid.NewGuid(), hoy.AddDays(-3), hoy.AddDays(-1), null)]);
        var handler = new ObtenerVisitasAnterioresDeCentroQueryHandler(contexto, new TrabajadoresQueryContextFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new(centro), CancellationToken.None);

        resultado.Select(v => v.Id).Should().Equal(cancelada.Id, reciente.Id, antigua.Id);
        resultado[0].EstaCancelada.Should().BeTrue();
        resultado[1].DocumentacionGestionada.Should().BeTrue();
    }

    [Fact]
    public async Task Un_centro_no_visible_no_devuelve_visitas()
    {
        var centro = Guid.NewGuid();
        var contexto = new VisitasFalsas();
        contexto.Lista.Add(new Visita(centro, DiaDeNegocio.Hoy().AddDays(-2), DiaDeNegocio.Hoy().AddDays(-1), null));
        var handler = new ObtenerVisitasAnterioresDeCentroQueryHandler(contexto, new TrabajadoresQueryContextFalso(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: []));

        (await handler.Handle(new(centro), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Los_nombres_se_acotan_a_los_trabajadores_visibles_y_a_la_visita()
    {
        var centro = Guid.NewGuid();
        var contexto = new VisitasFalsas();
        var visita = new Visita(centro, DiaDeNegocio.Hoy().AddDays(-2), DiaDeNegocio.Hoy().AddDays(-1), null);
        contexto.Lista.Add(visita);
        var trabajadores = new TrabajadoresQueryContextFalso();
        var visible = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "Pérez", "11111111H");
        var oculto = Trabajador.DeEmpresa(Guid.NewGuid(), "Luis", "García", "22222222J");
        trabajadores.ListaTrabajadores.AddRange([visible, oculto]);
        contexto.Participaciones.AddRange([new(visita.Id, visible.Id), new(visita.Id, oculto.Id)]);
        var handler = new ObtenerVisitasAnterioresDeCentroQueryHandler(contexto, trabajadores,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro], trabajadorIdsVisibles: [visible.Id]));

        var resultado = await handler.Handle(new(centro), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Trabajadores.Should().Equal("Ana Pérez");
    }
}
