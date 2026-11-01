using System.IO.Compression;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class LimitesImportacionTests
{
    private const string TiposOtraRuta =
        "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/otra/hoja.bin' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>";

    [Fact]
    public async Task Semaforo_limita_espera_libera_y_respeta_cancelacion()
    {
        var tenant = Guid.NewGuid();
        using var limitador = new LimitadorAnalisisImportacion(Options.Create(new OpcionesLimitesImportacion { AnalisisConcurrentes = 1, EsperaSegundos = 0 }));
        var permiso = await limitador.IntentarEntrarAsync(tenant);
        permiso.Should().NotBeNull();
        (await limitador.IntentarEntrarAsync(tenant)).Should().BeNull();
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limitador.IntentarEntrarAsync(tenant, cancelado.Token));
        permiso!.Dispose();
        permiso.Dispose();
        using var siguiente = await limitador.IntentarEntrarAsync(tenant);
        siguiente.Should().NotBeNull();
    }

    [Theory]
    [InlineData("<row r='1'><c r='A1'/></row><row r='2'><c r='A2'/></row>", 1, 10, true)]
    [InlineData("<row r='1'><c r='A1'/><c r='B1'/></row>", 10, 1, false)]
    [InlineData("<row r='100'><c r='A100'/></row>", 10, 10, true)]
    [InlineData("<row><c r='A100'/></row>", 10, 10, true)]
    public void Rechaza_antes_de_abrir_el_libro(string contenido, int filas, int celdas, bool limiteFilas)
    {
        using var flujo = CrearPaquete("otra/hoja.bin", TiposOtraRuta, contenido);
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoFilasPorHoja = filas, MaximoCeldasPorHoja = celdas });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>().Which.Filas.Should().Be(limiteFilas);
        flujo.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("xl/worksheets/sheet1.xml", null)]
    [InlineData("xl/worksheets/sheet1.xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>")]
    [InlineData("xl/worksheets/sheet1.dat", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>")]
    [InlineData("hoja-sin-extension", null)]
    public void Los_topes_no_dependen_de_Content_Types_ni_del_nombre_de_la_parte(string ruta, string? tipos)
    {
        using var flujo = CrearPaquete(ruta, tipos, "<row r='1'><c r='A1'/><c r='B1'/></row>");
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoCeldasPorHoja = 1 });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>().Which.Filas.Should().BeFalse();
    }

    [Fact]
    public void Los_topes_ignoran_la_capitalizacion_de_Content_Types()
    {
        using var flujo = new MemoryStream();
        using (var zip = new ZipArchive(flujo, ZipArchiveMode.Create, true))
        {
            Escribir(zip, "[content_types].xml", TiposOtraRuta);
            Escribir(zip, "xl/worksheets/sheet1.xml", Hoja("<row r='1'><c r='A1'/><c r='B1'/></row>"));
        }
        flujo.Position = 0;
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoCeldasPorHoja = 1 });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>();
    }

    [Fact]
    public void Acumula_las_celdas_de_todas_las_hojas_del_libro()
    {
        using var flujo = new MemoryStream();
        using (var zip = new ZipArchive(flujo, ZipArchiveMode.Create, true))
        {
            Escribir(zip, "xl/worksheets/sheet1.xml", Hoja("<row r='1'><c r='A1'/><c r='B1'/></row>"));
            Escribir(zip, "xl/worksheets/sheet2.xml", Hoja("<row r='1'><c r='A1'/><c r='B1'/></row>"));
        }
        flujo.Position = 0;
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoCeldasPorHoja = 2, MaximoCeldasPorLibro = 3 });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>().Which.Filas.Should().BeFalse();
    }

    [Theory]
    [InlineData("<row r='3000000000'><c r='A1'/></row>")]
    [InlineData("<row r='abc'><c r='A1'/></row>")]
    [InlineData("<row r='1'><c r='A99999999999'/></row>")]
    public void Rechaza_una_referencia_de_fila_ilegible_o_fuera_de_rango(string contenido)
    {
        using var flujo = CrearPaquete("xl/worksheets/sheet1.xml", null, contenido);
        var accion = () => InspectorXlsxSeguro.Verificar(flujo);
        accion.Should().Throw<ArchivoRechazadoException>();
    }

    [Fact]
    public void Un_xml_mal_formado_tras_la_raiz_es_un_rechazo_controlado_sin_citar_el_archivo()
    {
        using var flujo = CrearPaquete("xl/worksheets/sheet1.xml", null, "<row r='1'><c r='A1'></row>");
        var accion = () => InspectorXlsxSeguro.Verificar(flujo);
        accion.Should().ThrowExactly<ArchivoRechazadoException>().Which.Message.Should().NotContain("row");
    }

    [Fact]
    public void Una_hoja_con_DOCTYPE_no_escapa_a_los_topes_ni_expande_entidades()
    {
        var xml = "<!DOCTYPE worksheet [<!ENTITY a 'b'>]><worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1'/><c r='B1'/></row></sheetData></worksheet>";
        using var flujo = CrearPaquete("xl/worksheets/sheet1.xml", null, null, xml);
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoCeldasPorHoja = 1 });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>();
    }
    [Fact]
    public void Acepta_limites_exactos_por_hoja_incluso_con_ruta_no_convencional()
    {
        using var flujo = CrearPaquete("otra/hoja.bin", TiposOtraRuta, "<row r='1'><c r='A1'/></row>");
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoFilasPorHoja = 1, MaximoCeldasPorHoja = 1 });
        accion.Should().NotThrow();
    }

    [Fact]
    public void Un_libro_real_con_varias_hojas_y_columnas_pasa_con_los_topes_por_defecto()
    {
        using var libro = new XLWorkbook();
        foreach (var nombre in new[] { "Trabajadores", "Empresas", "Documentos" })
        {
            var hoja = libro.AddWorksheet(nombre);
            for (var fila = 1; fila <= 2_000; fila++)
                for (var columna = 1; columna <= 25; columna++)
                    hoja.Cell(fila, columna).Value = $"{nombre}-{fila}-{columna}";
            hoja.Cell(1, 1).CreateComment().AddText("comentario"); // vmlDrawing y comments
            hoja.Cell(2, 26).CreateDataValidation().List("a,b,c");
        }
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;

        var accion = () => InspectorXlsxSeguro.Verificar(flujo);

        accion.Should().NotThrow();
        flujo.Position.Should().Be(0);
    }

    [Fact]
    public void Una_tabla_de_cadenas_compartidas_desmesurada_se_rechaza()
    {
        using var flujo = new MemoryStream();
        using (var zip = new ZipArchive(flujo, ZipArchiveMode.Create, true))
            Escribir(zip, "xl/sharedStrings.xml",
                "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><si><t>a</t></si><si><t>b</t></si><si><t>c</t></si></sst>");
        flujo.Position = 0;
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoCeldasPorLibro = 2 });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>().Which.Filas.Should().BeFalse();
    }

    [Theory]
    [InlineData("hoja.bin", "UTF-16LE sin marca")]
    [InlineData("hoja.bin", "relleno inicial")]
    [InlineData("hoja.bin", "UTF-16BE sin marca")]
    public void Los_topes_valen_para_una_hoja_con_codificacion_o_relleno_no_habituales(string ruta, string caso)
    {
        var xml = Hoja("<row r='1'><c r='A1'/><c r='B1'/></row>");
        var bytes = caso switch
        {
            "UTF-16LE sin marca" => Encoding.Unicode.GetBytes(xml),
            "UTF-16BE sin marca" => Encoding.BigEndianUnicode.GetBytes(xml),
            _ => Encoding.UTF8.GetBytes(new string(' ', 1000) + xml)
        };
        using var flujo = new MemoryStream();
        using (var zip = new ZipArchive(flujo, ZipArchiveMode.Create, true))
        {
            using var entrada = zip.CreateEntry(ruta).Open();
            entrada.Write(bytes);
        }
        flujo.Position = 0;
        var accion = () => InspectorXlsxSeguro.Verificar(flujo, new OpcionesLimitesImportacion { MaximoCeldasPorHoja = 1 });
        accion.Should().ThrowExactly<LimiteHojaImportacionException>();
    }

    [Fact]
    public void Una_parte_que_no_es_xml_legible_se_ignora_y_no_tumba_un_libro_bueno()
    {
        using var flujo = new MemoryStream();
        using (var zip = new ZipArchive(flujo, ZipArchiveMode.Create, true))
        {
            Escribir(zip, "xl/worksheets/sheet1.xml", Hoja("<row r='1'><c r='A1'/></row>"));
            Escribir(zip, "xl/customXml/vacia.xml", "");
            Escribir(zip, "xl/media/binaria.bin", "\u0000\u0001\u0002 no es xml");
            Escribir(zip, "xl/drawings/vmlDrawing1.vml", "<xml><v:shape>sin cerrar");
        }
        flujo.Position = 0;
        var accion = () => InspectorXlsxSeguro.Verificar(flujo);
        accion.Should().NotThrow();
    }

    [Fact]
    public async Task Cada_servicio_de_analisis_aplica_los_topes_configurados()
    {
        using var libro = new XLWorkbook();
        var hoja = libro.AddWorksheet("Datos");
        hoja.Cell(1, 1).Value = "a";
        hoja.Cell(1, 2).Value = "b";
        using var paquete = new MemoryStream();
        libro.SaveAs(paquete);
        var opciones = Options.Create(new OpcionesLimitesImportacion { MaximoCeldasPorHoja = 1 });
        var analisis = new Dictionary<string, Func<Stream, Task>>
        {
            ["cae"] = flujo => new ClosedXmlImportacionParser(null!, null!, null!, null!, null!, null!, Options.Create(new OpcionesPlantillaCaeCompleta()), opciones).AnalizarAsync(flujo),
            ["clientes"] = flujo => new ClosedXmlPlantillaClientesService(null!, null!, opciones).AnalizarAsync(flujo),
            ["combinada"] = flujo => new ClosedXmlPlantillaCombinadaService(null!, null!, null!, opciones).AnalizarAsync(flujo),
            ["documentos"] = flujo => new ClosedXmlPlantillaDocumentosService(null!, null!, null!, opciones).AnalizarAsync(flujo)
        };

        foreach (var (nombre, analizar) in analisis)
        {
            paquete.Position = 0;
            var accion = () => analizar(paquete);
            await accion.Should().ThrowExactlyAsync<LimiteHojaImportacionException>(nombre);
        }
    }
    [Fact]
    public void Las_opciones_invalidas_no_se_aceptan()
    {
        new OpcionesLimitesImportacion().EsValida().Should().BeTrue();
        new OpcionesLimitesImportacion { AnalisisConcurrentes = 0 }.EsValida().Should().BeFalse();
        new OpcionesLimitesImportacion { AnalisisConcurrentesPorTenant = 0 }.EsValida().Should().BeFalse();
        new OpcionesLimitesImportacion { AnalisisConcurrentesPorTenant = 3 }.EsValida().Should().BeFalse();
        new OpcionesLimitesImportacion { EsperaSegundos = int.MaxValue }.EsValida().Should().BeFalse();
        new OpcionesLimitesImportacion { MaximoCeldasPorLibro = 0 }.EsValida().Should().BeFalse();
    }

    private static string Hoja(string contenido) =>
        $"<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData>{contenido}</sheetData></worksheet>";

    private static MemoryStream CrearPaquete(string rutaHoja, string? tipos, string? contenido, string? xmlCompleto = null)
    {
        var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, true))
        {
            if (tipos is not null) Escribir(zip, "[Content_Types].xml", tipos);
            Escribir(zip, rutaHoja, xmlCompleto ?? Hoja(contenido!));
        }
        memoria.Position = 0;
        return memoria;
    }

    private static void Escribir(ZipArchive zip, string nombre, string contenido)
    {
        using var escritor = new StreamWriter(zip.CreateEntry(nombre).Open(), new UTF8Encoding(false));
        escritor.Write(contenido);
    }
}
