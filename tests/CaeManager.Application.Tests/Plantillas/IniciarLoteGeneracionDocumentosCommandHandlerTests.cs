using CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class IniciarLoteGeneracionDocumentosCommandHandlerTests
{
    private static (PlantillaDocumentoRepositorioFalso Plantillas, PlantillaDocumentoVersionRepositorioFalso Versiones, PlantillaDocumentoVersion Version)
        ConstruirPlantillaConfirmada(AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador)
    {
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha", ambito, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var plantillas = new PlantillaDocumentoRepositorioFalso();
        plantillas.Agregar(plantilla);

        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        version.EstablecerElementos([new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", FuenteDatoPlantilla.Constante, "x")]);
        version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);

        return (plantillas, versiones, version);
    }

    [Fact]
    public async Task Crea_el_lote_y_un_item_por_cada_trabajador()
    {
        var (plantillas, versiones, version) = ConstruirPlantillaConfirmada();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        var usuarioId = Guid.NewGuid();
        var handler = new IniciarLoteGeneracionDocumentosCommandHandler(
            versiones, plantillas, lotes, items, new CurrentUserServiceFalso(usuarioId), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());
        var trabajadorIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var resultado = await handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(version.Id, trabajadorIds), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        lotes.Lista.Should().ContainSingle();
        lotes.Lista[0].TotalItems.Should().Be(3);
        lotes.Lista[0].UsuarioSolicitanteId.Should().Be(usuarioId);
        items.Lista.Should().HaveCount(3);
        items.Lista.Select(i => i.TrabajadorId).Should().BeEquivalentTo(trabajadorIds);
        resultado.Valor.ItemIds.Should().HaveCount(3);
    }

    [Fact]
    public async Task Deduplica_trabajadores_repetidos()
    {
        var (plantillas, versiones, version) = ConstruirPlantillaConfirmada();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        var handler = new IniciarLoteGeneracionDocumentosCommandHandler(
            versiones, plantillas, lotes, items, new CurrentUserServiceFalso(Guid.NewGuid()), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());
        var trabajadorId = Guid.NewGuid();

        var resultado = await handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(version.Id, [trabajadorId, trabajadorId]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        items.Lista.Should().ContainSingle();
    }

    [Fact]
    public async Task Rechaza_una_plantilla_que_no_es_de_ambito_trabajador()
    {
        var (plantillas, versiones, version) = ConstruirPlantillaConfirmada(AmbitoAplicacion.Empresa);
        var handler = new IniciarLoteGeneracionDocumentosCommandHandler(
            versiones, plantillas, new LoteGeneracionDocumentoRepositorioFalso(), new ItemGeneracionDocumentoRepositorioFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(version.Id, [Guid.NewGuid()]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteSoloTrabajador");
    }

    [Fact]
    public async Task Rechaza_una_version_sin_confirmar()
    {
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var plantillas = new PlantillaDocumentoRepositorioFalso();
        plantillas.Agregar(plantilla);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new IniciarLoteGeneracionDocumentosCommandHandler(
            versiones, plantillas, new LoteGeneracionDocumentoRepositorioFalso(), new ItemGeneracionDocumentoRepositorioFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(version.Id, [Guid.NewGuid()]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoConfirmada");
    }

    // ---- Asignación de Cartera: la plantilla y todos los Trabajadores del lote, antes de persistir nada

    private static readonly Guid CentroDeMiCartera = Guid.NewGuid();
    private static readonly Guid ClienteDeMiCartera = Guid.NewGuid();

    private sealed class EscenarioLote
    {
        public required IniciarLoteGeneracionDocumentosCommandHandler Handler { get; init; }
        public required PlantillaDocumentoVersion Version { get; init; }
        public required LoteGeneracionDocumentoRepositorioFalso Lotes { get; init; }
        public required ItemGeneracionDocumentoRepositorioFalso Items { get; init; }
        public required UnitOfWorkFalso UnitOfWork { get; init; }
    }

    private static EscenarioLote PrepararLote(
        AlcanceDatosServiceFalso alcance, Guid? centroId, Guid? clienteId, bool confirmada = true)
    {
        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid(),
            centroId: centroId, clienteId: clienteId);
        var plantillas = new PlantillaDocumentoRepositorioFalso();
        plantillas.Agregar(plantilla);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        version.EstablecerElementos([new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", FuenteDatoPlantilla.Constante, "x")]);
        if (confirmada)
            version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);

        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        return new EscenarioLote
        {
            Handler = new IniciarLoteGeneracionDocumentosCommandHandler(
                versiones, plantillas, lotes, items, new CurrentUserServiceFalso(Guid.NewGuid()), alcance, unitOfWork),
            Version = version,
            Lotes = lotes,
            Items = items,
            UnitOfWork = unitOfWork,
        };
    }

    private static AlcanceDatosServiceFalso GestorConCarteraExplicita(params Guid[] trabajadoresVisibles) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDeMiCartera], centroIdsVisibles: [CentroDeMiCartera],
            trabajadorIdsVisibles: trabajadoresVisibles);

    private static void NoSePersistioNada(EscenarioLote e)
    {
        e.Lotes.Lista.Should().BeEmpty();
        e.Items.Lista.Should().BeEmpty("no nace un lote a medias");
        e.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Un_Gestor_con_cartera_explicita_no_inicia_un_lote_con_una_plantilla_fuera_de_ella(bool centroFuera, bool clienteFuera)
    {
        var trabajador = Guid.NewGuid();
        var e = PrepararLote(
            GestorConCarteraExplicita(trabajador),
            centroId: centroFuera ? Guid.NewGuid() : CentroDeMiCartera,
            clienteId: clienteFuera ? Guid.NewGuid() : ClienteDeMiCartera);

        var resultado = await e.Handler.Handle(new IniciarLoteGeneracionDocumentosCommand(e.Version.Id, [trabajador]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada", "fuera de cartera no se delata que la versión existe");
        NoSePersistioNada(e);
    }

    [Fact]
    public async Task La_cartera_se_comprueba_antes_que_el_estado_de_la_version_para_no_delatar_una_version_sin_confirmar()
    {
        var trabajador = Guid.NewGuid();
        var e = PrepararLote(GestorConCarteraExplicita(trabajador), centroId: Guid.NewGuid(), clienteId: null, confirmada: false);

        var resultado = await e.Handler.Handle(new IniciarLoteGeneracionDocumentosCommand(e.Version.Id, [trabajador]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada");
        NoSePersistioNada(e);
    }

    [Fact]
    public async Task Un_Trabajador_fuera_de_cartera_entre_otros_visibles_rechaza_el_lote_entero_sin_persistir_nada()
    {
        var visibleUno = Guid.NewGuid();
        var visibleDos = Guid.NewGuid();
        var e = PrepararLote(GestorConCarteraExplicita(visibleUno, visibleDos), CentroDeMiCartera, ClienteDeMiCartera);

        var resultado = await e.Handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(e.Version.Id, [visibleUno, Guid.NewGuid(), visibleDos]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.PropietarioNoEncontrado");
        NoSePersistioNada(e);
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_inicia_un_lote_con_plantilla_y_Trabajadores_de_su_cartera()
    {
        var visibleUno = Guid.NewGuid();
        var visibleDos = Guid.NewGuid();
        var e = PrepararLote(GestorConCarteraExplicita(visibleUno, visibleDos), CentroDeMiCartera, ClienteDeMiCartera);

        var resultado = await e.Handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(e.Version.Id, [visibleUno, visibleDos]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        e.Items.Lista.Select(i => i.TrabajadorId).Should().BeEquivalentTo([visibleUno, visibleDos]);
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_inicia_el_lote_con_cualquier_plantilla_y_Trabajador()
    {
        var e = PrepararLote(new AlcanceDatosServiceFalso(tieneAccesoTotal: true), Guid.NewGuid(), Guid.NewGuid());

        var resultado = await e.Handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(e.Version.Id, [Guid.NewGuid(), Guid.NewGuid()]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        e.Items.Lista.Should().HaveCount(2);
        e.UnitOfWork.VecesGuardado.Should().Be(1);
    }
}
