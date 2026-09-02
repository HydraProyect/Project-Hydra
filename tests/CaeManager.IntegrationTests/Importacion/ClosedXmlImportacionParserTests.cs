using System.Text;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Contrato «nada se descarta en silencio» (IMPORTACION.md § 3 bis, ratificado
/// por DCR-12 decisión B, propietario 2026-08-24) sobre el paso de ANÁLISIS
/// (<see cref="ClosedXmlImportacionParser.AnalizarAsync"/>) — la misma
/// invariante que REC-045 (PR #419) cerró en el handler de escritura.
///
/// Un test por causal, no una comprobación genérica: la decisión instruye no
/// uniformizar las ramas de descarte, así que cada test exige el motivo de SU
/// rama. Los tests de "caso legítimo" prueban lo contrario y con la misma
/// importancia: que la fila de plantilla vacía, la celda de fecha vacía y el
/// centro sin marca siguen sin generar ninguna entrada — si dejaran de estarlo,
/// cualquier Excel real produciría decenas de avisos no accionables.
/// </summary>
public class ClosedXmlImportacionParserTests
{
    private const string DniValido = "12345678Z";
    private const string TipoDocumentoConocido = "Certificado de aptitud médica"; // Columna G (7) — ver ColumnasDocumentos.

    [Fact]
    public async Task Fila_de_Empleados_sin_nombre_ni_apellidos_pero_con_DNI_queda_omitida_con_su_motivo()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        hoja.Cell(4, 1).Value = 1;
        hoja.Cell(4, 4).Value = DniValido; // Solo el DNI: nombre y apellidos vacíos.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Empleados").Subject;
        omitido.Fila.Should().Be(4);
        omitido.Motivo.Should().Contain("Faltan datos obligatorios");
        plan.Trabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_Empleados_totalmente_vacia_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        hoja.Cell(4, 1).Value = 1; // Solo el número de fila — el resto de la fila, en blanco.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Trabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Fecha_de_documento_ilegible_queda_omitida_nombrando_el_valor_bruto()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 7).Value = "No aplica"; // Columna G: texto, no una fecha.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Descripcion.Contains(TipoDocumentoConocido)).Subject;
        omitido.Hoja.Should().Be("Empleados");
        omitido.Fila.Should().Be(4);
        omitido.Motivo.Should().Contain("No aplica");
        omitido.Motivo.Should().Contain("no se pudo interpretar");
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Celda_de_fecha_vacia_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4); // Ninguna columna de documento recibe valor.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Tipo_de_documento_fuera_del_catalogo_queda_omitido_y_no_en_Advertencias()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 7).Value = DateTime.UtcNow.Date.AddDays(-30); // Fecha válida, pero el tipo no está en el catálogo (fake sin sembrar).

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Descripcion.Contains(TipoDocumentoConocido)).Subject;
        omitido.Motivo.Should().Contain("No existe este tipo de documento en el catálogo");
        plan.Advertencias.Should().NotContain(a => a.Descripcion.Contains(TipoDocumentoConocido));
        plan.Advertencias.Should().NotContain(a => a.Motivo.Contains("se omitió"));
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_Asignaciones_sin_nombre_ni_apellidos_pero_con_marca_de_centro_queda_omitida()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1;
        hojaAsignaciones.Cell(5, 4).Value = "X"; // Marca la única columna de centro, sin nombre ni apellidos.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Asignaciones").Subject;
        omitido.Fila.Should().Be(5);
        omitido.Motivo.Should().Contain("Faltan nombre o apellidos");
        plan.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_Asignaciones_totalmente_vacia_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1; // Solo el número de fila — sin nombre, apellidos ni marcas.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().NotContain(o => o.Hoja == "Asignaciones" && o.Fila == 5);
        plan.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Columna_de_centro_sin_marca_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaEmpleados = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hojaEmpleados, fila: 4, nombre: "Ana", apellidos: "García");

        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1;
        hojaAsignaciones.Cell(5, 2).Value = "Ana";
        hojaAsignaciones.Cell(5, 3).Value = "García";
        // Columna 4 (única columna de centro) queda sin marcar a propósito.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().NotContain(o => o.Hoja == "Asignaciones");
        plan.Asignaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Las cuatro hojas que <see cref="ClosedXmlImportacionParser.AnalizarAsync"/>
    /// espera siempre, vacías: sin esto, la ausencia de cualquiera de ellas
    /// registraría su propio "Hoja completa no encontrada" en Omitidos y
    /// contaminaría el test de la causal que de verdad se quiere aislar.
    /// </summary>
    private static XLWorkbook NuevoLibroBase()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Centros_Plataformas");
        libro.Worksheets.Add("Empleados");
        libro.Worksheets.Add("Extranjeros (Ibertec GmbH)");
        libro.Worksheets.Add("Asignaciones");
        return libro;
    }

    private static void EscribirTrabajadorValido(IXLWorksheet hoja, int fila, string nombre = "Marta", string apellidos = "Ruiz")
    {
        hoja.Cell(fila, 1).Value = fila - 3;
        hoja.Cell(fila, 2).Value = nombre;
        hoja.Cell(fila, 3).Value = apellidos;
        hoja.Cell(fila, 4).Value = DniValido;
    }

    /// <summary>
    /// Da de alta un único Centro en Centros_Plataformas y su columna
    /// correspondiente (por posición) en la cabecera de Asignaciones — el
    /// mínimo que exige <c>AnalizarAsignaciones</c> para no descartar la hoja
    /// entera por descuadre de columnas.
    /// </summary>
    private static void ConfigurarUnCentro(XLWorkbook libro, string nombreCentro)
    {
        var hojaCentros = libro.Worksheets.Worksheet("Centros_Plataformas");
        hojaCentros.Cell(5, 2).Value = nombreCentro;

        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(4, 4).Value = "Centro 1";
        hojaAsignaciones.Cell(4, 5).Value = "TOTAL CENTROS";
    }

    private static async Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(XLWorkbook libro)
    {
        var parser = new ClosedXmlImportacionParser(
            new AsignacionesQueryContextFalso(), new CentrosQueryContextFalso(), new DocumentosQueryContextFalso(),
            new EmpresasQueryContextFalso(), new TiposDocumentoQueryContextFalso(), new TrabajadoresQueryContextFalso());

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await parser.AnalizarAsync(flujo);
    }
}
