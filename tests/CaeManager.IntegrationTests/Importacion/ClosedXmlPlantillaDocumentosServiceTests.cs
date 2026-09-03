using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis, DCR-12
/// B) sobre <see cref="ClosedXmlPlantillaDocumentosService.AnalizarAsync"/>
/// — auditada por REC-129. Antes de este incremento, tipo de documento
/// ausente, fecha de emisión ausente y fecha de emisión ilegible compartían
/// un único motivo genérico ("Faltan datos obligatorios"), que llegaba a
/// acusar de ausente una fecha que en realidad estaba presente pero
/// ilegible — DCR-12 B prohíbe uniformizar así los motivos, y este archivo
/// prueba que ya no ocurre.
/// </summary>
public class ClosedXmlPlantillaDocumentosServiceTests
{
    private const string DniValido = "12345678Z";
    private const string TipoDocumentoConocido = "Certificado de aptitud médica";

    [Fact]
    public async Task Fila_de_ejemplo_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Documentos");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(2, 2).Value = TipoDocumentoConocido;
        hoja.Cell(2, 3).Value = DateTime.UtcNow.Date.AddDays(-1);

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Falta_el_tipo_de_documento_queda_omitida_con_motivo_propio()
    {
        var libro = NuevoLibroBase(DniValido);
        var hoja = libro.Worksheets.Worksheet("Documentos");
        hoja.Cell(2, 1).Value = DniValido;
        hoja.Cell(2, 3).Value = DateTime.UtcNow.Date.AddDays(-1); // Fecha presente y válida, solo falta el tipo.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(2);
        omitido.Motivo.Should().Be("Falta el tipo de documento.");
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Falta_la_fecha_de_emision_queda_omitida_con_motivo_propio_distinto_de_ilegible()
    {
        var libro = NuevoLibroBase(DniValido);
        var hoja = libro.Worksheets.Worksheet("Documentos");
        hoja.Cell(2, 1).Value = DniValido;
        hoja.Cell(2, 2).Value = TipoDocumentoConocido; // Celda de fecha vacía a propósito.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(2);
        omitido.Motivo.Should().Be("Falta la fecha de emisión.");
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Fecha_de_emision_ilegible_queda_omitida_nombrando_el_valor_bruto_y_no_dice_falta()
    {
        var libro = NuevoLibroBase(DniValido);
        var hoja = libro.Worksheets.Worksheet("Documentos");
        hoja.Cell(2, 1).Value = DniValido;
        hoja.Cell(2, 2).Value = TipoDocumentoConocido;
        hoja.Cell(2, 3).Value = "No aplica"; // Presente, pero no es una fecha.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(2);
        omitido.Motivo.Should().Be("La fecha «No aplica» no se pudo interpretar como una fecha válida; no se importó este documento.");
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_valida_se_importa_como_Documento()
    {
        var libro = NuevoLibroBase(DniValido);
        var hoja = libro.Worksheets.Worksheet("Documentos");
        hoja.Cell(2, 1).Value = DniValido;
        hoja.Cell(2, 2).Value = TipoDocumentoConocido;
        hoja.Cell(2, 3).Value = DateTime.UtcNow.Date.AddDays(-1);

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        var documento = plan.Documentos.Should().ContainSingle().Subject;
        documento.Dni.Should().Be(DniValido);
        documento.NombreTipoDocumento.Should().Be(TipoDocumentoConocido);
    }

    [Fact]
    public async Task Dni_sin_trabajador_correspondiente_queda_omitido()
    {
        var libro = NuevoLibroBase(); // Sin sembrar ningún trabajador.
        var hoja = libro.Worksheets.Worksheet("Documentos");
        hoja.Cell(2, 1).Value = DniValido;
        hoja.Cell(2, 2).Value = TipoDocumentoConocido;
        hoja.Cell(2, 3).Value = DateTime.UtcNow.Date.AddDays(-1);

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Be("No existe ningún trabajador con este DNI. Da de alta al trabajador antes de importar sus documentos.");
        plan.Documentos.Should().BeEmpty();
    }

    private static XLWorkbook NuevoLibroBase(string? dniASembrar = null)
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Documentos");
        _dniASembrar = dniASembrar;
        return libro;
    }

    private static string? _dniASembrar;

    private static async Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(XLWorkbook libro)
    {
        var trabajadoresContext = new TrabajadoresQueryContextFalso();
        if (_dniASembrar is not null)
        {
            trabajadoresContext.ListaTrabajadores.Add(Trabajador.DeEmpresa(Guid.NewGuid(), "Marta", "Ruiz", _dniASembrar));
        }

        var tiposDocumentoContext = new TiposDocumentoQueryContextFalso();
        tiposDocumentoContext.ListaTiposDocumento.Add(new TipoDocumento(
            TipoDocumentoConocido, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambitoAplicacion: AmbitoAplicacion.Trabajador));

        var servicio = new ClosedXmlPlantillaDocumentosService(
            new DocumentosQueryContextFalso(), tiposDocumentoContext, trabajadoresContext);

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }
}
