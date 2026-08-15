using CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ConfirmarPlantillaDocumentoVersionCommandHandlerTests
{
    private static (PlantillaDocumento Documento, PlantillaDocumentoVersion Version) CrearPlantillaConVersion(bool conElementos = true)
    {
        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(documento.Id, 1, "url-falsa.pdf", new string('a', 64));
        if (conElementos)
            version.EstablecerElementos([new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo")]);
        return (documento, version);
    }

    [Fact]
    public async Task Confirma_la_version_y_la_deja_como_version_actual_de_la_plantilla()
    {
        var (documento, version) = CrearPlantillaConVersion();
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var usuarioId = Guid.NewGuid();
        var handler = new ConfirmarPlantillaDocumentoVersionCommandHandler(
            versiones, documentos, new CurrentUserServiceFalso(usuarioId), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Confirmada);
        version.ConfirmadaPorUsuarioId.Should().Be(usuarioId);
        documento.VersionActualId.Should().Be(version.Id);
    }

    [Fact]
    public async Task Rechaza_confirmar_una_version_sin_elementos()
    {
        var (documento, version) = CrearPlantillaConVersion(conElementos: false);
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new ConfirmarPlantillaDocumentoVersionCommandHandler(
            versiones, documentos, new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.SinElementos");
    }

    [Fact]
    public async Task Rechaza_confirmar_sin_usuario_actual_resuelto()
    {
        var (documento, version) = CrearPlantillaConVersion();
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new ConfirmarPlantillaDocumentoVersionCommandHandler(
            versiones, documentos, new CurrentUserServiceFalso(usuarioId: null), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.SinUsuarioActual");
    }

    [Fact]
    public async Task Rechaza_confirmar_una_version_ya_confirmada()
    {
        var (documento, version) = CrearPlantillaConVersion();
        version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new ConfirmarPlantillaDocumentoVersionCommandHandler(
            versiones, documentos, new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionYaConfirmada");
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_version_no_existe()
    {
        var handler = new ConfirmarPlantillaDocumentoVersionCommandHandler(
            new PlantillaDocumentoVersionRepositorioFalso(), new PlantillaDocumentoRepositorioFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada");
    }
}
