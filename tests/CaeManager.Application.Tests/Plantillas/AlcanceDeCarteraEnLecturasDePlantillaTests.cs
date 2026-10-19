using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Listar las plantillas y abrir una versión en el editor aplican la Asignación de Cartera con el mismo criterio con el
/// que se crean y se modifican (AlcanceDePlantillas): una plantilla atada a un Centro o a un Cliente empresarial fuera
/// del alcance no se lista y, abierta por Id, responde igual que una versión que no existe. Las plantillas generales del
/// Tenant siguen al alcance de todos.
/// </summary>
public class AlcanceDeCarteraEnLecturasDePlantillaTests
{
    public enum Ancla { Centro, ClienteEmpresarial, CentroDentroYClienteFuera }

    private readonly Guid _centroDeMiCartera = Guid.NewGuid();
    private readonly Guid _clienteDeMiCartera = Guid.NewGuid();
    private readonly PlantillasQueryContextFalso _contexto = new();

    private AlcanceDatosServiceFalso GestorConCarteraExplicita() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera], centroIdsVisibles: [_centroDeMiCartera]);

    private PlantillaDocumentoVersion Sembrar(string nombre, Guid? centroId, Guid? clienteId)
    {
        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, nombre, AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid(),
            centroId: centroId, clienteId: clienteId);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        _contexto.ListaPlantillasDocumento.Add(plantilla);
        _contexto.ListaPlantillasDocumentoVersion.Add(version);
        return version;
    }

    private (Guid? CentroId, Guid? ClienteId) FueraDeCartera(Ancla ancla) => ancla switch
    {
        Ancla.Centro => (Guid.NewGuid(), null),
        Ancla.ClienteEmpresarial => (null, Guid.NewGuid()),
        _ => (_centroDeMiCartera, Guid.NewGuid()),
    };

    private Task<IReadOnlyList<PlantillaDocumentoListaDto>> ListarAsync(IAlcanceDatosService alcance) =>
        new ObtenerPlantillasDocumentoQueryHandler(_contexto, alcance).Handle(new ObtenerPlantillasDocumentoQuery(), CancellationToken.None);

    [Theory]
    [InlineData(Ancla.Centro)]
    [InlineData(Ancla.ClienteEmpresarial)]
    [InlineData(Ancla.CentroDentroYClienteFuera)]
    public async Task Un_Gestor_con_cartera_explicita_no_ve_en_la_lista_una_plantilla_fuera_de_ella(Ancla ancla)
    {
        var (centroId, clienteId) = FueraDeCartera(ancla);
        Sembrar("Ajena", centroId, clienteId);
        Sembrar("De mi cartera", _centroDeMiCartera, _clienteDeMiCartera);
        Sembrar("General del Tenant", centroId: null, clienteId: null);

        var resultado = await ListarAsync(GestorConCarteraExplicita());

        resultado.Select(p => p.Nombre).Should().BeEquivalentTo(["De mi cartera", "General del Tenant"]);
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_la_lista_incluye_todas_las_plantillas()
    {
        Sembrar("Ajena", Guid.NewGuid(), Guid.NewGuid());
        Sembrar("General del Tenant", centroId: null, clienteId: null);

        var resultado = await ListarAsync(new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        resultado.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(Ancla.Centro)]
    [InlineData(Ancla.ClienteEmpresarial)]
    [InlineData(Ancla.CentroDentroYClienteFuera)]
    public async Task Un_Gestor_con_cartera_explicita_que_abre_una_version_fuera_de_ella_recibe_lo_mismo_que_si_no_existiera(Ancla ancla)
    {
        var (centroId, clienteId) = FueraDeCartera(ancla);
        var version = Sembrar("Ajena", centroId, clienteId);

        var resultado = await new ObtenerPlantillaDocumentoVersionQueryHandler(_contexto, GestorConCarteraExplicita())
            .Handle(new ObtenerPlantillaDocumentoVersionQuery(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada", "fuera de cartera no se delata que la versión existe");
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_abre_una_version_de_su_cartera_y_una_general_del_Tenant()
    {
        var deMiCartera = Sembrar("De mi cartera", _centroDeMiCartera, _clienteDeMiCartera);
        var general = Sembrar("General del Tenant", centroId: null, clienteId: null);
        var handler = new ObtenerPlantillaDocumentoVersionQueryHandler(_contexto, GestorConCarteraExplicita());

        (await handler.Handle(new ObtenerPlantillaDocumentoVersionQuery(deMiCartera.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
        (await handler.Handle(new ObtenerPlantillaDocumentoVersionQuery(general.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_abre_una_version_de_cualquier_plantilla()
    {
        var version = Sembrar("Ajena", Guid.NewGuid(), Guid.NewGuid());

        var resultado = await new ObtenerPlantillaDocumentoVersionQueryHandler(_contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true))
            .Handle(new ObtenerPlantillaDocumentoVersionQuery(version.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }
}
