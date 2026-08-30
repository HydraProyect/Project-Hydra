using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class AgregarVersionPlantillaCommandHandlerTests
{
    private static (PlantillaDocumentoRepositorioFalso Plantillas, PlantillaDocumentoVersionRepositorioFalso Versiones, PlantillaDocumento Plantilla)
        ConstruirPlantillaConVersionConfirmada(byte[] contenidoVersionUno, IEnumerable<PlantillaElemento>? elementos = null)
    {
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var plantillas = new PlantillaDocumentoRepositorioFalso();
        plantillas.Agregar(plantilla);

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contenidoVersionUno));
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url1.pdf", hash);
        version.EstablecerElementos(elementos ?? [new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 10, 20, 100, 15, "CIF", FuenteDatoPlantilla.EmpresaCif)]);
        version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);

        plantilla.EstablecerVersionActual(version.Id);

        return (plantillas, versiones, plantilla);
    }

    [Fact]
    public async Task Crea_una_nueva_version_con_el_siguiente_numero()
    {
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada([1, 2, 3]);
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, new FileStorageServiceFalso(), new UnitOfWorkFalso(), NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        var resultado = await handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, [4, 5, 6], "nueva.pdf"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        versiones.Lista.Should().HaveCount(2);
        var nueva = versiones.Lista.Single(v => v.Id == resultado.Valor.PlantillaDocumentoVersionId);
        nueva.NumeroVersion.Should().Be(2);
        nueva.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.PendienteRevision);
    }

    [Fact]
    public async Task Copia_los_elementos_de_la_version_actual()
    {
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada([1, 2, 3]);
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, new FileStorageServiceFalso(), new UnitOfWorkFalso(), NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        var resultado = await handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, [4, 5, 6], "nueva.pdf"), CancellationToken.None);

        var nueva = versiones.Lista.Single(v => v.Id == resultado.Valor.PlantillaDocumentoVersionId);
        nueva.Elementos.Should().ContainSingle(e => e.EtiquetaVisible == "CIF" && e.FuenteDato == FuenteDatoPlantilla.EmpresaCif);
    }

    [Fact]
    public async Task Detecta_cuando_el_archivo_es_identico_a_la_version_actual()
    {
        var contenido = new byte[] { 1, 2, 3 };
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada(contenido);
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, new FileStorageServiceFalso(), new UnitOfWorkFalso(), NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        var resultado = await handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, contenido, "identico.pdf"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ArchivoIdenticoAVersionAnterior.Should().BeTrue();
    }

    [Fact]
    public async Task No_marca_identico_cuando_el_archivo_cambio()
    {
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada([1, 2, 3]);
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, new FileStorageServiceFalso(), new UnitOfWorkFalso(), NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        var resultado = await handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, [9, 9, 9], "distinto.pdf"), CancellationToken.None);

        resultado.Valor.ArchivoIdenticoAVersionAnterior.Should().BeFalse();
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_plantilla_no_existe()
    {
        var handler = new AgregarVersionPlantillaCommandHandler(
            new PlantillaDocumentoRepositorioFalso(), new PlantillaDocumentoVersionRepositorioFalso(),
            new FileStorageServiceFalso(), new UnitOfWorkFalso(), NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        var resultado = await handler.Handle(
            new AgregarVersionPlantillaCommand(Guid.NewGuid(), [1], "nueva.pdf"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.NoEncontrada");
    }

    /// <summary>
    /// Auditoría de seguridad del módulo (2026-08-30), pendiente 3.5: dos
    /// altas de versión concurrentes sobre la misma plantilla pueden calcular
    /// el mismo siguienteNumeroVersion — la restricción única de BD lo
    /// traduce en un DbUpdateException (no de concurrencia optimista, dos
    /// inserts nuevos) que antes llegaba sin traducir al llamador.
    /// </summary>
    [Fact]
    public async Task Conflicto_de_numero_de_version_devuelve_fallo_legible_en_vez_de_reventar()
    {
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada([1, 2, 3]);
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new DbUpdateException("índice único violado") };
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, new FileStorageServiceFalso(), unitOfWork, NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        var resultado = await handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, [4, 5, 6], "nueva.pdf"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionEnConflicto");
    }

    /// <summary>El blob ya se había subido a almacenamiento antes de que fallara SaveChangesAsync — sin la limpieza, quedaría huérfano para siempre.</summary>
    [Fact]
    public async Task Conflicto_de_numero_de_version_limpia_el_blob_ya_subido()
    {
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada([1, 2, 3]);
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new DbUpdateException("índice único violado") };
        var almacenamiento = new FileStorageServiceFalso();
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, almacenamiento, unitOfWork, NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        await handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, [4, 5, 6], "nueva.pdf"), CancellationToken.None);

        almacenamiento.ArchivosGuardados.Should().Be(0);
    }

    /// <summary>Un choque de concurrencia optimista (DbUpdateConcurrencyException) no es el caso de esta pieza — debe seguir propagando para que ConcurrenciaBehavior lo traduzca, no confundirse con el conflicto de número de versión.</summary>
    [Fact]
    public async Task Concurrencia_optimista_no_la_captura_el_handler()
    {
        var (plantillas, versiones, plantilla) = ConstruirPlantillaConVersionConfirmada([1, 2, 3]);
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new DbUpdateConcurrencyException("token de versión distinto") };
        var handler = new AgregarVersionPlantillaCommandHandler(
            plantillas, versiones, new FileStorageServiceFalso(), unitOfWork, NullLogger<AgregarVersionPlantillaCommandHandler>.Instance);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => handler.Handle(
            new AgregarVersionPlantillaCommand(plantilla.Id, [4, 5, 6], "nueva.pdf"), CancellationToken.None));
    }
}
