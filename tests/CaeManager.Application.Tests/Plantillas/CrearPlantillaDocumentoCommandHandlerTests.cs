using CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class CrearPlantillaDocumentoCommandHandlerTests
{
    private static CrearPlantillaDocumentoCommand ComandoValido(Guid? centroId = null, Guid? clienteId = null) =>
        new("Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos,
            [1, 2, 3], "plantilla.pdf", CentroId: centroId, ClienteId: clienteId);

    [Fact]
    public async Task Crea_el_documento_y_su_primera_version_en_borrador()
    {
        var documentos = new PlantillaDocumentoRepositorioFalso();
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        var almacenamiento = new FileStorageServiceFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CrearPlantillaDocumentoCommandHandler(
            documentos, versiones, almacenamiento, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(ComandoValido(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documentos.Lista.Should().ContainSingle();
        documentos.Lista[0].Origen.Should().Be(OrigenPlantilla.Externa);
        versiones.Lista.Should().ContainSingle();
        versiones.Lista[0].NumeroVersion.Should().Be(1);
        versiones.Lista[0].EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Borrador);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Guarda_el_archivo_en_el_almacenamiento_y_lo_referencia_desde_la_version()
    {
        var documentos = new PlantillaDocumentoRepositorioFalso();
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        var almacenamiento = new FileStorageServiceFalso();
        var handler = new CrearPlantillaDocumentoCommandHandler(
            documentos, versiones, almacenamiento, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        versiones.Lista[0].ArchivoOriginalUrl.Should().NotBeNullOrWhiteSpace();
        versiones.Lista[0].HashSha256ArchivoOriginal.Should().HaveLength(64);
    }

    [Fact]
    public async Task Rechaza_un_centro_fuera_de_la_cartera_visible()
    {
        var centroAjeno = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);
        var handler = new CrearPlantillaDocumentoCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            new FileStorageServiceFalso(), alcance, new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(centroId: centroAjeno), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.CentroSinAcceso");
    }

    [Fact]
    public async Task Rechaza_un_cliente_fuera_de_la_cartera_visible()
    {
        var clienteAjeno = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new CrearPlantillaDocumentoCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            new FileStorageServiceFalso(), alcance, new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(clienteId: clienteAjeno), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ClienteSinAcceso");
    }
}
