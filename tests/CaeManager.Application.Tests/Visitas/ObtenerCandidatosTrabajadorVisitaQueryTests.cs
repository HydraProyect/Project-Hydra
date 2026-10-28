using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerCandidatosTrabajadorVisita;
using CaeManager.Domain.Centros;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// S-B-1 (decisión del propietario, 2026-10-28): el selector de Trabajadores de una Visita solo
/// ofrece a los de la Empresa gestionada activa —la Empresa del Centro y sus Subcontratas con
/// Relación Empresarial vigente— que además estén en la cartera de quien pregunta. Pide el
/// selector con alcance <see cref="AlcanceSelectorTrabajadores.Cartera"/>, nunca la base general.
/// </summary>
public class ObtenerCandidatosTrabajadorVisitaQueryTests
{
    private readonly VisitasQueryContextFalso _visitas = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly Centro _centro = new(Guid.NewGuid(), Guid.NewGuid(), "Nave norte");
    private readonly Visita _visita;
    private readonly Trabajador _propio;
    private readonly Trabajador _yaEntra;
    private readonly Trabajador _deSubcontrataVigente;
    private readonly Trabajador _deOtraEmpresa;
    private readonly Trabajador _deSubcontrataDeOtraEmpresa;

    public ObtenerCandidatosTrabajadorVisitaQueryTests()
    {
        _visita = new Visita(_centro.Id, DiaDeNegocioHoy().AddDays(1), DiaDeNegocioHoy().AddDays(2), null);
        var subcontrata = Guid.NewGuid();
        var subcontrataAjena = Guid.NewGuid();
        _propio = Trabajador.DeEmpresa(_centro.EmpresaId, "Ana", "Garcia", "12345678Z");
        _yaEntra = Trabajador.DeEmpresa(_centro.EmpresaId, "Luis", "Perez", "77189989B");
        _deSubcontrataVigente = Trabajador.DeSubcontrata(subcontrata, "Carla", "Ruiz", "00000002W");
        _deOtraEmpresa = Trabajador.DeEmpresa(Guid.NewGuid(), "Bruno", "Sanz", "00000003A");
        _deSubcontrataDeOtraEmpresa = Trabajador.DeSubcontrata(subcontrataAjena, "Dario", "Vidal", "00000004G");

        _visitas.ListaVisitas.Add(_visita);
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(_visita.Id, _yaEntra.Id));
        _centros.ListaCentros.Add(_centro);
        _trabajadores.ListaTrabajadores.AddRange([_propio, _yaEntra, _deSubcontrataVigente, _deOtraEmpresa, _deSubcontrataDeOtraEmpresa]);
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrata, _centro.EmpresaId, DateTime.UtcNow));
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrataAjena, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public async Task Solo_ofrece_Trabajadores_de_la_Empresa_de_la_Visita_y_de_su_Subcontrata_vigente_menos_quien_ya_entra()
    {
        var emisor = new EmisorDeSelector(_trabajadores, _empresas, new AlcanceDatosServiceFalso());

        var candidatos = await Consultar(new AlcanceDatosServiceFalso(), emisor);

        candidatos.Select(c => c.Id).Should().BeEquivalentTo([_propio.Id, _deSubcontrataVigente.Id]);
        emisor.AlcancesPedidos.Should().Equal(AlcanceSelectorTrabajadores.Cartera);
    }

    [Fact]
    public async Task No_ofrece_a_un_Trabajador_de_la_Empresa_fuera_de_la_cartera_de_quien_pregunta()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [_deSubcontrataVigente.Id, _deOtraEmpresa.Id]);

        var candidatos = await Consultar(alcance, new EmisorDeSelector(_trabajadores, _empresas, alcance));

        candidatos.Select(c => c.Id).Should().BeEquivalentTo([_deSubcontrataVigente.Id]);
    }

    [Fact]
    public async Task No_ofrece_a_un_Trabajador_de_una_Subcontrata_cuya_Relacion_Empresarial_con_la_Empresa_ya_se_cerro()
    {
        var subcontrataCerrada = Guid.NewGuid();
        var deRelacionCerrada = Trabajador.DeSubcontrata(subcontrataCerrada, "Elena", "Mora", "00000005M");
        _trabajadores.ListaTrabajadores.Add(deRelacionCerrada);
        var relacion = RelacionEmpresarial.Crear(subcontrataCerrada, _centro.EmpresaId, DateTime.UtcNow.AddDays(-30));
        relacion.Cerrar(DateTime.UtcNow);
        _empresas.ListaRelacionesEmpresariales.Add(relacion);

        var candidatos = await Consultar(new AlcanceDatosServiceFalso(), new EmisorDeSelector(_trabajadores, _empresas, new AlcanceDatosServiceFalso()));

        candidatos.Select(c => c.Id).Should().NotContain(deRelacionCerrada.Id)
            .And.BeEquivalentTo([_propio.Id, _deSubcontrataVigente.Id]);
    }

    private Task<IReadOnlyList<TrabajadorSelectorDto>> Consultar(AlcanceDatosServiceFalso alcance, ISender emisor) =>
        new ObtenerCandidatosTrabajadorVisitaQueryHandler(_visitas, _centros, _trabajadores, _empresas, alcance, emisor)
            .Handle(new ObtenerCandidatosTrabajadorVisitaQuery(_visita.Id), CancellationToken.None);

    private static DateOnly DiaDeNegocioHoy() => Domain.Common.DiaDeNegocio.Hoy();

    /// <summary>ISender mínimo: responde el selector de Trabajadores con el handler real y apunta el alcance pedido.</summary>
    private sealed class EmisorDeSelector(
        TrabajadoresQueryContextFalso trabajadores, EmpresasQueryContextFalso empresas, AlcanceDatosServiceFalso alcance) : ISender
    {
        public List<AlcanceSelectorTrabajadores> AlcancesPedidos { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerTrabajadoresParaSelectorQuery q)
                throw new NotSupportedException(request.GetType().Name);
            AlcancesPedidos.Add(q.Alcance);
            return (TResponse)(object)await new ObtenerTrabajadoresParaSelectorQueryHandler(trabajadores, empresas, alcance)
                .Handle(q, cancellationToken);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }
}
