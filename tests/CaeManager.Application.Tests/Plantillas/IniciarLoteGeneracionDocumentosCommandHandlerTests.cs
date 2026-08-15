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
            versiones, plantillas, lotes, items, new CurrentUserServiceFalso(usuarioId), new UnitOfWorkFalso());
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
            versiones, plantillas, lotes, items, new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());
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
            new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());

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
            new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new IniciarLoteGeneracionDocumentosCommand(version.Id, [Guid.NewGuid()]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoConfirmada");
    }
}
