using CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class GuardarElementosPlantillaCommandHandlerTests
{
    private static PlantillaDocumentoVersion VersionBorrador() =>
        new(Guid.NewGuid(), 1, "url-falsa.pdf", new string('a', 64));

    private static readonly ElementoPlantillaEntradaDto ElementoDto =
        new(TipoElementoPlantilla.Texto, 1, 10, 20, 100, 15, "CIF", FuenteDatoPlantilla.EmpresaCif, null, null, false, null, "campoCif");

    [Fact]
    public async Task Reemplaza_el_conjunto_de_elementos_y_marca_pendiente_de_revision()
    {
        var version = VersionBorrador();
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new GuardarElementosPlantillaCommandHandler(versiones, new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new GuardarElementosPlantillaCommand(version.Id, [ElementoDto]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        version.Elementos.Should().ContainSingle();
        version.Elementos[0].EtiquetaVisible.Should().Be("CIF");
        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.PendienteRevision);
    }

    [Fact]
    public async Task Un_segundo_guardado_reemplaza_por_completo_el_conjunto_anterior()
    {
        var version = VersionBorrador();
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new GuardarElementosPlantillaCommandHandler(versiones, new UnitOfWorkFalso());
        await handler.Handle(new GuardarElementosPlantillaCommand(version.Id, [ElementoDto]), CancellationToken.None);

        var otroElemento = ElementoDto with { EtiquetaVisible = "Nombre" };
        var resultado = await handler.Handle(
            new GuardarElementosPlantillaCommand(version.Id, [otroElemento]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        version.Elementos.Should().ContainSingle();
        version.Elementos[0].EtiquetaVisible.Should().Be("Nombre");
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_version_no_existe()
    {
        var handler = new GuardarElementosPlantillaCommandHandler(
            new PlantillaDocumentoVersionRepositorioFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new GuardarElementosPlantillaCommand(Guid.NewGuid(), [ElementoDto]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada");
    }

    [Fact]
    public async Task Rechaza_editar_una_version_ya_confirmada()
    {
        var version = VersionBorrador();
        version.EstablecerElementos([new PlantillaElemento(
            version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo")]);
        version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = new GuardarElementosPlantillaCommandHandler(versiones, new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new GuardarElementosPlantillaCommand(version.Id, [ElementoDto]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionYaConfirmada");
    }
}
