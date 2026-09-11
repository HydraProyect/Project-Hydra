using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Asignaciones;

/// <summary>
/// Defecto detectado en la revisión de Codex del 2026-09-11 sobre la lista
/// Trabajadores Gen 2: el handler consultaba Trabajador/Centro por los Ids
/// recibidos sin comprobar cartera, así que un llamador que construyera Ids
/// fuera de su ámbito (RLS aísla por tenant, no por cartera) obtenía nombre y
/// documentos faltantes de un Centro ajeno antes de que
/// <c>CrearAsignacionesCommand</c> lo rechazara.
///
/// Acota por <see cref="CaeManager.Application.Common.IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync"/>
/// — mismo criterio que <c>ObtenerCentrosParaSelectorQuery</c>, el selector
/// que en la práctica alimenta esta consulta— y deja el Trabajador sin
/// acotar a propósito (ver el comentario del handler): acotarlo con
/// <c>ObtenerTrabajadorIdsVisiblesAsync</c> rompería el alta de un Trabajador
/// todavía sin ninguna Asignación, que es precisamente el caso de uso del
/// preflight.
/// </summary>
public class ObtenerDocumentosFaltantesParaAsignacionQueryHandlerTests
{
    private readonly TrabajadoresQueryContextFalso _trabajadoresContext = new();
    private readonly CentrosQueryContextFalso _centrosContext = new();
    private readonly DocumentosFaltantesServiceFalso _servicio = new();

    private readonly Guid _clienteId = Guid.NewGuid();
    private readonly Guid _empresaId = Guid.NewGuid();

    [Fact]
    public async Task Descarta_en_silencio_los_centros_fuera_de_cartera()
    {
        var centroEnAmbito = new Centro(_clienteId, _empresaId, "Centro de mi cartera");
        var centroAjeno = new Centro(_clienteId, _empresaId, "Centro de otro gestor");
        var trabajador = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", "77189989B");

        _centrosContext.ListaCentros.AddRange([centroEnAmbito, centroAjeno]);
        _trabajadoresContext.ListaTrabajadores.Add(trabajador);

        var handler = CrearHandler(centroIdsVisibles: [centroEnAmbito.Id]);

        var resultado = await handler.Handle(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajador.Id], [centroEnAmbito.Id, centroAjeno.Id]),
            CancellationToken.None);

        _servicio.ParejasRecibidas.Should().ContainSingle()
            .Which.CentroId.Should().Be(centroEnAmbito.Id, "el centro ajeno no está en la cartera de quien consulta");
        resultado.Should().OnlyContain(d => d.CentroId == centroEnAmbito.Id);
    }

    [Fact]
    public async Task Devuelve_vacio_sin_llamar_al_servicio_si_ningun_centro_esta_en_cartera()
    {
        var centroAjeno = new Centro(_clienteId, _empresaId, "Centro de otro gestor");
        var trabajador = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", "77189989B");

        _centrosContext.ListaCentros.Add(centroAjeno);
        _trabajadoresContext.ListaTrabajadores.Add(trabajador);

        var handler = CrearHandler(centroIdsVisibles: []);

        var resultado = await handler.Handle(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajador.Id], [centroAjeno.Id]),
            CancellationToken.None);

        resultado.Should().BeEmpty();
        _servicio.ParejasRecibidas.Should().BeEmpty();
    }

    [Fact]
    public async Task No_acota_el_trabajador_aunque_no_tenga_ninguna_asignacion_visible()
    {
        // Control negativo de la decisión de diseño: un Trabajador ausente de
        // ObtenerTrabajadorIdsVisiblesAsync (nunca tuvo una Asignación activa a
        // un Centro visible) debe seguir pasando el preflight — es justo el
        // caso "alta por primera vez" del drawer N×M y de "Asignar desde
        // visita".
        var centroEnAmbito = new Centro(_clienteId, _empresaId, "Centro de mi cartera");
        var trabajadorNuevo = Trabajador.DeEmpresa(_empresaId, "Luis", "Perez", "12345678Z");

        _centrosContext.ListaCentros.Add(centroEnAmbito);
        _trabajadoresContext.ListaTrabajadores.Add(trabajadorNuevo);

        var handler = CrearHandler(centroIdsVisibles: [centroEnAmbito.Id], trabajadorIdsVisibles: []);

        var resultado = await handler.Handle(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajadorNuevo.Id], [centroEnAmbito.Id]),
            CancellationToken.None);

        _servicio.ParejasRecibidas.Should().ContainSingle(p => p.TrabajadorId == trabajadorNuevo.Id);
        resultado.Should().Contain(d => d.TrabajadorId == trabajadorNuevo.Id);
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_pasan_todos_los_centros()
    {
        var centroA = new Centro(_clienteId, _empresaId, "Centro A");
        var centroB = new Centro(_clienteId, _empresaId, "Centro B");
        var trabajador = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", "77189989B");

        _centrosContext.ListaCentros.AddRange([centroA, centroB]);
        _trabajadoresContext.ListaTrabajadores.Add(trabajador);

        var handler = CrearHandler(tieneAccesoTotal: true);

        await handler.Handle(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajador.Id], [centroA.Id, centroB.Id]),
            CancellationToken.None);

        _servicio.ParejasRecibidas.Should().HaveCount(2);
    }

    private ObtenerDocumentosFaltantesParaAsignacionQueryHandler CrearHandler(
        bool tieneAccesoTotal = false, IReadOnlyList<Guid>? centroIdsVisibles = null, IReadOnlyList<Guid>? trabajadorIdsVisibles = null) =>
        new(_trabajadoresContext, _centrosContext, _servicio,
            new AlcanceDatosServiceFalso(
                tieneAccesoTotal: tieneAccesoTotal, centroIdsVisibles: centroIdsVisibles, trabajadorIdsVisibles: trabajadorIdsVisibles));
}

public class DocumentosFaltantesServiceFalso : IDocumentosFaltantesService
{
    public List<ParejaTrabajadorCentro> ParejasRecibidas { get; } = [];

    public Task<IReadOnlyList<DocumentoFaltanteDto>> CalcularAsync(
        IReadOnlyList<ParejaTrabajadorCentro> parejas, CancellationToken cancellationToken)
    {
        ParejasRecibidas.AddRange(parejas);
        return Task.FromResult<IReadOnlyList<DocumentoFaltanteDto>>(
            parejas.Select(p => new DocumentoFaltanteDto(
                p.TrabajadorId, p.TrabajadorNombre, p.CentroId, p.CentroNombre, Guid.NewGuid(), "Documento de prueba")).ToList());
    }
}
