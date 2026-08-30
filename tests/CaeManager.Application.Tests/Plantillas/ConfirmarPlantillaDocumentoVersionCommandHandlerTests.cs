using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ConfirmarPlantillaDocumentoVersionCommandHandlerTests
{
    /// <summary>
    /// PdfVisual por defecto a propósito: estos tests cubren el flujo de
    /// confirmación en general (elementos, usuario, estado), no el cotejo
    /// contra AcroForm — ese cotejo solo aplica a PdfConCampos y tiene sus
    /// propios tests más abajo, con el extractor y el archivo real de por
    /// medio.
    /// </summary>
    private static (PlantillaDocumento Documento, PlantillaDocumentoVersion Version) CrearPlantillaConVersion(
        bool conElementos = true, FormatoOrigenPlantilla formato = FormatoOrigenPlantilla.PdfVisual, string? nombreCampoAcroForm = null)
    {
        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, formato, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(documento.Id, 1, "url-falsa.pdf", new string('a', 64));
        if (conElementos)
            version.EstablecerElementos([new PlantillaElemento(
                version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", nombreCampoAcroForm: nombreCampoAcroForm)]);
        return (documento, version);
    }

    private static ConfirmarPlantillaDocumentoVersionCommandHandler CrearHandler(
        PlantillaDocumentoVersionRepositorioFalso versiones, PlantillaDocumentoRepositorioFalso documentos,
        Guid? usuarioId = null, IReadOnlyList<CampoAcroFormDetectado>? camposReales = null) =>
        new(versiones, documentos, new CurrentUserServiceFalso(usuarioId ?? Guid.NewGuid()),
            new AlmacenamientoFalso(), new ExtractorAcroFormFalso(camposReales ?? []), new UnitOfWorkFalso());

    private sealed class ExtractorAcroFormFalso(IReadOnlyList<CampoAcroFormDetectado> campos) : IExtractorCamposAcroFormService
    {
        public IReadOnlyList<CampoAcroFormDetectado> Extraer(byte[] pdfOriginal) => campos;
    }

    /// <summary>El contenido es irrelevante para estos tests: el cotejo lo decide <see cref="ExtractorAcroFormFalso"/>, no el PDF real.</summary>
    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
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
        var handler = CrearHandler(versiones, documentos, usuarioId);

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
        var handler = CrearHandler(versiones, documentos);

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
            versiones, documentos, new CurrentUserServiceFalso(usuarioId: null),
            new AlmacenamientoFalso(), new ExtractorAcroFormFalso([]), new UnitOfWorkFalso());

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
        var handler = CrearHandler(versiones, documentos);

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionYaConfirmada");
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_version_no_existe()
    {
        var handler = CrearHandler(new PlantillaDocumentoVersionRepositorioFalso(), new PlantillaDocumentoRepositorioFalso());

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada");
    }

    /// <summary>
    /// Auditoría de seguridad del módulo (2026-08-30), pendientes 3.2+3.3:
    /// caso feliz de PdfConCampos — el NombreCampoAcroForm del elemento
    /// coincide con un campo real del PDF, así que confirma con normalidad.
    /// </summary>
    [Fact]
    public async Task Confirma_una_plantilla_acroform_cuando_el_campo_coincide_con_el_pdf_real()
    {
        var (documento, version) = CrearPlantillaConVersion(formato: FormatoOrigenPlantilla.PdfConCampos, nombreCampoAcroForm: "txtCif");
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = CrearHandler(versiones, documentos,
            camposReales: [new CampoAcroFormDetectado("txtCif", 1, 0, 0, 10, 10)]);

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Confirmada);
    }

    /// <summary>
    /// El caso que motivó esta pieza: antes, un NombreCampoAcroForm que no
    /// existe en el PDF pasaba la confirmación sin aviso y
    /// RellenadorPlantillaPdfService lo descartaba en silencio al generar.
    /// </summary>
    [Fact]
    public async Task Rechaza_confirmar_una_plantilla_acroform_cuyo_campo_no_existe_en_el_pdf()
    {
        var (documento, version) = CrearPlantillaConVersion(formato: FormatoOrigenPlantilla.PdfConCampos, nombreCampoAcroForm: "txtNoExiste");
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = CrearHandler(versiones, documentos,
            camposReales: [new CampoAcroFormDetectado("txtCif", 1, 0, 0, 10, 10)]);

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.CamposAcroFormInexistentes");
        version.EstadoConfiguracion.Should().NotBe(EstadoConfiguracionPlantilla.Confirmada);
    }

    /// <summary>Dos elementos apuntando al mismo campo del PDF: uno pisaría el valor del otro al generar, sin ningún aviso.</summary>
    [Fact]
    public async Task Rechaza_confirmar_una_plantilla_acroform_con_dos_elementos_en_el_mismo_campo()
    {
        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(documento.Id, 1, "url-falsa.pdf", new string('a', 64));
        version.EstablecerElementos([
            new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo A", nombreCampoAcroForm: "txtCif"),
            new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 0, 20, 10, 10, "Campo B", nombreCampoAcroForm: "txtCif"),
        ]);
        var documentos = new PlantillaDocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);
        var handler = CrearHandler(versiones, documentos,
            camposReales: [new CampoAcroFormDetectado("txtCif", 1, 0, 0, 10, 10)]);

        var resultado = await handler.Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.CamposAcroFormDuplicados");
    }
}
