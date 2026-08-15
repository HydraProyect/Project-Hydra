using CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class CrearPlantillaDocumentoCommandHandlerTests
{
    private static (TiposDocumentoQueryContextFalso Contexto, Guid TipoDocumentoId) ContextoConTipoDocumentoDeTrabajador()
    {
        var contexto = new TiposDocumentoQueryContextFalso();
        var tipo = new TipoDocumento("Ficha de riesgos", null, false, 1, AmbitoAplicacion.Trabajador);
        contexto.ListaTiposDocumento.Add(tipo);
        return (contexto, tipo.Id);
    }

    private static CrearPlantillaDocumentoCommand ComandoValido(Guid tipoDocumentoId, Guid? centroId = null, Guid? clienteId = null) =>
        new("Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos,
            tipoDocumentoId, [1, 2, 3], "plantilla.pdf", CentroId: centroId, ClienteId: clienteId);

    [Fact]
    public async Task Crea_el_documento_y_su_primera_version_en_borrador()
    {
        var (tiposDocumentoContexto, tipoDocumentoId) = ContextoConTipoDocumentoDeTrabajador();
        var documentos = new PlantillaDocumentoRepositorioFalso();
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        var almacenamiento = new FileStorageServiceFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CrearPlantillaDocumentoCommandHandler(
            documentos, versiones, tiposDocumentoContexto, almacenamiento, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(ComandoValido(tipoDocumentoId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documentos.Lista.Should().ContainSingle();
        documentos.Lista[0].Origen.Should().Be(OrigenPlantilla.Externa);
        documentos.Lista[0].TipoDocumentoId.Should().Be(tipoDocumentoId);
        versiones.Lista.Should().ContainSingle();
        versiones.Lista[0].NumeroVersion.Should().Be(1);
        versiones.Lista[0].EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Borrador);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Guarda_el_archivo_en_el_almacenamiento_y_lo_referencia_desde_la_version()
    {
        var (tiposDocumentoContexto, tipoDocumentoId) = ContextoConTipoDocumentoDeTrabajador();
        var documentos = new PlantillaDocumentoRepositorioFalso();
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        var almacenamiento = new FileStorageServiceFalso();
        var handler = new CrearPlantillaDocumentoCommandHandler(
            documentos, versiones, tiposDocumentoContexto, almacenamiento, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(tipoDocumentoId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        versiones.Lista[0].ArchivoOriginalUrl.Should().NotBeNullOrWhiteSpace();
        versiones.Lista[0].HashSha256ArchivoOriginal.Should().HaveLength(64);
    }

    [Fact]
    public async Task Rechaza_un_centro_fuera_de_la_cartera_visible()
    {
        var (tiposDocumentoContexto, tipoDocumentoId) = ContextoConTipoDocumentoDeTrabajador();
        var centroAjeno = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);
        var handler = new CrearPlantillaDocumentoCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            tiposDocumentoContexto, new FileStorageServiceFalso(), alcance, new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(tipoDocumentoId, centroId: centroAjeno), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.CentroSinAcceso");
    }

    [Fact]
    public async Task Rechaza_un_cliente_fuera_de_la_cartera_visible()
    {
        var (tiposDocumentoContexto, tipoDocumentoId) = ContextoConTipoDocumentoDeTrabajador();
        var clienteAjeno = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new CrearPlantillaDocumentoCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            tiposDocumentoContexto, new FileStorageServiceFalso(), alcance, new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(tipoDocumentoId, clienteId: clienteAjeno), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ClienteSinAcceso");
    }

    [Fact]
    public async Task Rechaza_un_tipo_de_documento_inexistente()
    {
        var handler = new CrearPlantillaDocumentoCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            new TiposDocumentoQueryContextFalso(), new FileStorageServiceFalso(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.TipoDocumentoNoEncontrado");
    }

    [Fact]
    public async Task Rechaza_un_tipo_de_documento_de_otro_ambito()
    {
        var contexto = new TiposDocumentoQueryContextFalso();
        var tipoDeEmpresa = new TipoDocumento("RLC", null, false, 1, AmbitoAplicacion.Empresa);
        contexto.ListaTiposDocumento.Add(tipoDeEmpresa);
        var handler = new CrearPlantillaDocumentoCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            contexto, new FileStorageServiceFalso(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(tipoDeEmpresa.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.AmbitoIncorrecto");
    }
}
