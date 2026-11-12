using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Lectura, detección y extracción sobre PDF reales fabricados aquí, con datos inventados: PDFsharp abre y reparte, y
/// PdfPig lee el texto. Sin servicios externos.
/// </summary>
public sealed class DivisionSobrePdfRealTests
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid HaciendaId = Guid.NewGuid();
    private static readonly Guid FormacionId = Guid.NewGuid();
    private static readonly Guid AnaId = Guid.NewGuid();

    private static readonly CatalogoDivisionPdf Catalogo = new(
        new EmpresaDivisionPdf(EmpresaId, "Montajes Norte S.L.", "B12345678"), [],
        [new TrabajadorDivisionPdf(AnaId, "Ana", "Perez Gil", null, EmpresaId)], [],
        [
            new TipoDocumentoDivisionPdf(HaciendaId, "Certificado de Hacienda", AmbitoAplicacion.Empresa, false, null, []),
            new TipoDocumentoDivisionPdf(FormacionId, "Formacion PRL", AmbitoAplicacion.Trabajador, false, null, []),
        ]);

    private readonly PdfSharpDivisorPdf _divisor = new(NullLogger<PdfSharpDivisorPdf>.Instance);
    private readonly PdfPigExtractorPalabrasPdf _extractor = new(NullLogger<PdfPigExtractorPalabrasPdf>.Instance);

    /// <summary>Un PDF con una página por entrada; una entrada vacía es una página sin texto.</summary>
    private static byte[] Pdf(params string[][] paginas)
    {
        var constructor = new PdfDocumentBuilder();
        var fuente = constructor.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var lineas in paginas)
        {
            var pagina = constructor.AddPage(595, 842);
            for (var i = 0; i < lineas.Length; i++)
                pagina.AddText(lineas[i], i == 0 ? 18 : 10, new PdfPoint(40, 800 - (i * 24)), fuente);
        }

        return constructor.Build();
    }

    private static readonly string[] Hacienda = ["Certificado de Hacienda", "Titular Montajes Norte", "CIF B-12345678"];
    private static readonly string[] Formacion = ["Formacion PRL", "Alumna Ana Perez Gil", "Curso de 60 horas"];

    private IReadOnlyList<TramoDetectado> Detectar(byte[] pdf)
    {
        using var abierto = _divisor.Abrir(pdf).Valor;
        var palabras = _extractor.ExtraerPalabras(pdf, LimitesDivisionPdf.TamanoMaximoOriginalBytes);
        palabras.EsExitoso.Should().BeTrue(palabras.Error.Codigo);

        return DetectorTramosPdf.Detectar(palabras.Valor, abierto.NumeroPaginas, Catalogo);
    }

    [Fact]
    public void Un_PDF_digital_con_varios_documentos_se_parte_con_tipo_titular_y_motivo()
    {
        var tramos = Detectar(Pdf(Hacienda, ["Anexo sin cabecera", "mas texto de relleno", "Pagina 2 de 2"], Formacion));

        tramos.Should().HaveCount(2);
        tramos[0].Should().BeEquivalentTo(new TramoDetectado(
            1, 2, HaciendaId, "Certificado de Hacienda", ClaseTitular.EmpresaGestionada, EmpresaId, "Montajes Norte S.L.",
            MotivoDeteccion.CifEmpresaGestionada, 1));
        tramos[1].Should().BeEquivalentTo(new TramoDetectado(
            3, 3, FormacionId, "Formacion PRL", ClaseTitular.Trabajador, AnaId, "Ana Perez Gil", MotivoDeteccion.Nombre, 3));
    }

    [Fact]
    public void Un_PDF_sin_texto_queda_entero_sin_identificar()
    {
        var tramos = Detectar(Pdf([], [], []));

        tramos.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new TramoDetectado(1, 3, null, null, null, null, null, MotivoDeteccion.SinTexto, null));
    }

    [Fact]
    public void Un_PDF_mezclado_separa_lo_digital_de_lo_escaneado()
    {
        var tramos = Detectar(Pdf(Hacienda, [], [], Formacion));

        tramos.Select(t => (t.PaginaInicial, t.PaginaFinal, t.Motivo)).Should().Equal(
            (1, 1, MotivoDeteccion.CifEmpresaGestionada),
            (2, 3, MotivoDeteccion.SinTexto),
            (4, 4, MotivoDeteccion.Nombre));
    }

    [Fact]
    public void Extraer_un_rango_conserva_el_numero_de_paginas_y_su_contenido()
    {
        var pdf = Pdf(Hacienda, ["Segunda pagina del paquete"], ["Tercera pagina del paquete"], Formacion);
        using var abierto = _divisor.Abrir(pdf).Valor;

        var tramo = abierto.ExtraerPaginas(2, 3, long.MaxValue);

        tramo.EsExitoso.Should().BeTrue();
        var paginas = _extractor.ExtraerPalabras(tramo.Valor).Valor;
        paginas.Should().HaveCount(2);
        Texto(paginas[0]).Should().Be("Segunda pagina del paquete");
        Texto(paginas[1]).Should().Be("Tercera pagina del paquete");
    }

    private static string Texto(PaginaPdf pagina) =>
        string.Join(' ', pagina.Palabras.Select(p => p.Texto.Trim()).Where(t => t.Length > 0));

    [Fact]
    public void Del_mismo_PDF_abierto_salen_varios_tramos_y_todo_el_rango_es_valido()
    {
        var pdf = Pdf(Hacienda, Formacion, Hacienda);
        using var abierto = _divisor.Abrir(pdf).Valor;

        Paginas(abierto.ExtraerPaginas(1, 1, long.MaxValue).Valor).Should().Be(1);
        Paginas(abierto.ExtraerPaginas(2, 3, long.MaxValue).Valor).Should().Be(2);
        Paginas(abierto.ExtraerPaginas(1, 3, long.MaxValue).Valor).Should().Be(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    public void Un_rango_fuera_del_PDF_falla(int inicial, int final)
    {
        using var abierto = _divisor.Abrir(Pdf(Hacienda, Formacion, Hacienda)).Valor;

        abierto.ExtraerPaginas(inicial, final, long.MaxValue).Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido);
    }

    [Fact]
    public void Un_PDF_firmado_se_marca_para_avisar_y_uno_normal_no()
    {
        var normal = Pdf(Hacienda);
        var firmado = normal.Concat(Encoding.ASCII.GetBytes("\n%/ByteRange [0 1 2 3]\n")).ToArray();

        using (var abierto = _divisor.Abrir(normal).Valor)
            abierto.EstaFirmado.Should().BeFalse();

        using (var abierto = _divisor.Abrir(firmado).Valor)
            abierto.EstaFirmado.Should().BeTrue();
    }

    [Fact]
    public void Un_PDF_con_contrasena_se_distingue_de_uno_roto()
    {
        var protegido = ConContrasena(Pdf(Hacienda));

        _divisor.Abrir(protegido).Error.Codigo.Should().Be(ErroresDivisorPdf.ConContrasena);
        _divisor.Abrir(Encoding.ASCII.GetBytes("esto no es un PDF")).Error.Codigo.Should().Be(ErroresDivisorPdf.Ilegible);
        _divisor.Abrir([]).Error.Codigo.Should().Be(ErroresDivisorPdf.Ilegible);
    }

    [Fact]
    public void Acepta_originales_de_hasta_50_MB_y_rechaza_los_mayores()
    {
        var justo = Rellenar(Pdf(Hacienda), LimitesDivisionPdf.TamanoMaximoOriginalBytes);

        using (var abierto = _divisor.Abrir(justo).Valor)
            abierto.NumeroPaginas.Should().Be(1);

        _extractor.ExtraerPalabras(justo).Error.Codigo.Should().Be("ExtractorPalabrasPdf.ArchivoDemasiadoGrande",
            "el tope de 10 MB de los demás llamadores no cambia");
        _extractor.ExtraerPalabras(justo, LimitesDivisionPdf.TamanoMaximoOriginalBytes).EsExitoso.Should().BeTrue();

        var pasado = Rellenar(Pdf(Hacienda), LimitesDivisionPdf.TamanoMaximoOriginalBytes + 1);
        _divisor.Abrir(pasado).Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
        _extractor.ExtraerPalabras(pasado, LimitesDivisionPdf.TamanoMaximoOriginalBytes).EsFallido.Should().BeTrue();
    }

    [Fact]
    public void Un_tramo_que_se_expande_por_encima_del_original_falla_cerrado_al_descargarlo()
    {
        var original = PdfQueSeExpandeAlReescribirse(elementos: 100_000);
        using (var abierto = _divisor.Abrir(original).Valor)
        {
            // Sin cota, el tramo pasa con mucho del máximo que se reserva para descargarlo: esta prueba sí lo ejercita.
            ((long)abierto.ExtraerPaginas(1, 1, long.MaxValue).Valor.Length).Should().BeGreaterThan(LimitesDivisionPdf.TamanoMaximoTramo(original.Length));
        }

        // Lo que hace el proceso aparte con un tramo de Descargar: abrir el original y extraer con la cota de la descarga.
        var descarga = _divisor.ExtraerTramo(original, 1, 1, LimitesDivisionPdf.TamanoMaximoTramo(original.Length));

        descarga.Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
    }

    [Fact]
    public void La_cota_del_tramo_se_aplica_mientras_se_escribe_y_un_tramo_justo_en_la_cota_sale_entero()
    {
        using var abierto = _divisor.Abrir(Pdf(Hacienda, Formacion)).Valor;
        var sinCota = abierto.ExtraerPaginas(1, 2, long.MaxValue).Valor;

        abierto.ExtraerPaginas(1, 2, sinCota.Length - 1).Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
        abierto.ExtraerPaginas(1, 2, sinCota.Length).Valor.Length.Should().Be(sinCota.Length);
    }

    [Fact]
    public void El_flujo_acotado_no_crece_por_encima_de_su_cota_y_corta_antes_de_copiar_la_escritura_que_la_pasaria()
    {
        using var flujo = new FlujoMemoriaAcotado(1000);
        flujo.Write(new byte[600]);

        var pasar = () => flujo.Write(new byte[401], 0, 401);

        pasar.Should().Throw<IOException>();
        flujo.CotaSuperada.Should().BeTrue();
        flujo.Length.Should().Be(600, "la escritura que pasaría la cota no copia nada");
        flujo.Capacity.Should().BeLessThanOrEqualTo(1000);
        flujo.Write(new byte[400], 0, 400);
        flujo.Length.Should().Be(1000);
        flujo.Capacity.Should().Be(1000, "el búfer dobla, pero nunca por encima de la cota");
    }

    [Fact]
    public void El_flujo_acotado_corta_tambien_el_byte_suelto_y_el_cambio_de_longitud_que_pasarian_la_cota()
    {
        using var flujo = new FlujoMemoriaAcotado(10);
        flujo.Write(new byte[9]);
        flujo.WriteByte(1);
        flujo.Length.Should().Be(10, "el byte que llega justo a la cota cabe");

        var otroByte = () => flujo.WriteByte(2);
        otroByte.Should().Throw<IOException>();
        flujo.Length.Should().Be(10, "el byte que pasaría la cota no se escribe");
        flujo.CotaSuperada.Should().BeTrue();

        using var alargado = new FlujoMemoriaAcotado(10);
        alargado.SetLength(10);
        alargado.Length.Should().Be(10);
        var alargar = () => alargado.SetLength(11);
        alargar.Should().Throw<IOException>();
        alargado.Length.Should().Be(10, "alargar por encima de la cota no reserva nada");
        alargado.Capacity.Should().BeLessThanOrEqualTo(10);
        alargado.CotaSuperada.Should().BeTrue();
    }

    [Fact]
    public void Un_guardado_que_se_traga_el_corte_de_la_cota_y_termina_falla_igual_como_demasiado_grande()
    {
        // PDFsharp podría capturar la excepción del flujo y seguir: lo que quedó escrito no es un tramo completo.
        var tragado = PdfSharpDivisorPdf.GuardarConCota(salida =>
        {
            salida.Write(new byte[8]);
            try
            {
                salida.Write(new byte[8]);
            }
            catch (IOException)
            {
            }
        }, cota: 10);

        tragado.Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
        PdfSharpDivisorPdf.GuardarConCota(salida => salida.Write(new byte[10]), cota: 10).Valor.Should().HaveCount(10);
    }

    [Fact]
    public void Abrir_rechaza_antes_de_PDFsharp_un_PDF_cuya_estructura_comprimida_pasa_de_los_topes()
    {
        var bomba = PdfQueSeExpandeAlReescribirse(elementos: 10, objetosDeclarados: LimitesUnionPdf.MaximoObjetosPorPdf + 1);

        _divisor.Abrir(bomba).Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoComplejo);
        _divisor.ExtraerTramo(bomba, 1, 1, LimitesDivisionPdf.TamanoMaximoTramo(bomba.Length)).Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoComplejo,
            "extraer un tramo en el proceso aparte abre con la misma cota");

        using var normal = _divisor.Abrir(PdfQueSeExpandeAlReescribirse(elementos: 10)).Valor;
        normal.NumeroPaginas.Should().Be(1, "un flujo de objetos dentro de los topes se sigue abriendo");
    }

    [Fact]
    public void Un_PDF_cifrado_que_el_acotador_no_puede_medir_se_rechaza_como_PDF_con_contrasena_sin_abrirlo()
    {
        // Sus flujos de objetos irían cifrados: no se miden, y dejarlo pasar haría de «/Encrypt» un salto del acotador.
        var bomba = PdfQueSeExpandeAlReescribirse(elementos: 10, objetosDeclarados: LimitesUnionPdf.MaximoObjetosPorPdf + 1);
        var cifrada = bomba.Concat(Encoding.ASCII.GetBytes("% /Encrypt\n")).ToArray();

        _divisor.Abrir(cifrada).Error.Codigo.Should().Be(ErroresDivisorPdf.ConContrasena);
    }

    /// <summary>
    /// PDF 1.5 cuya única página guarda sus recursos en un flujo de objetos comprimido, con la tabla de referencias también
    /// como flujo. PDFsharp lo abre y, al reescribir el tramo, saca esos recursos sin comprimir: el tramo ocupa mucho más
    /// que el original, como pasa con PDF reales que comprimen sus objetos.
    /// </summary>
    private static byte[] PdfQueSeExpandeAlReescribirse(int elementos, int objetosDeclarados = 1)
    {
        const string cabeceraFlujoObjetos = "4 0 ";
        var recursos = Encoding.ASCII.GetBytes(cabeceraFlujoObjetos + "<< /Relleno [" + string.Join(' ', Enumerable.Repeat("0", elementos)) + "] >>");
        byte[] comprimido;
        using (var destino = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(destino, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(recursos);
            comprimido = destino.ToArray();
        }

        using var pdf = new MemoryStream();
        var desplazamientos = new long[7];
        void Escribir(string texto) => pdf.Write(Encoding.ASCII.GetBytes(texto));
        void Objeto(int numero, string cuerpo)
        {
            desplazamientos[numero] = pdf.Position;
            Escribir($"{numero} 0 obj\n{cuerpo}\nendobj\n");
        }

        Escribir("%PDF-1.5\n");
        Objeto(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Objeto(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Objeto(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources 4 0 R >>");
        desplazamientos[5] = pdf.Position;
        Escribir($"5 0 obj\n<< /Type /ObjStm /N {objetosDeclarados} /First {cabeceraFlujoObjetos.Length} /Filter /FlateDecode /Length {comprimido.Length} >>\nstream\n");
        pdf.Write(comprimido);
        Escribir("\nendstream\nendobj\n");
        desplazamientos[6] = pdf.Position;

        // /W [1 4 2]: tipo, desplazamiento (o flujo de objetos) y generación (o índice dentro del flujo), en big-endian.
        var entradas = new List<byte>();
        void Entrada(byte tipo, long campo, int indice)
        {
            entradas.AddRange([tipo, (byte)(campo >> 24), (byte)(campo >> 16), (byte)(campo >> 8), (byte)campo, (byte)(indice >> 8), (byte)indice]);
        }

        Entrada(0, 0, 0xFFFF);
        Entrada(1, desplazamientos[1], 0);
        Entrada(1, desplazamientos[2], 0);
        Entrada(1, desplazamientos[3], 0);
        Entrada(2, 5, 0);
        Entrada(1, desplazamientos[5], 0);
        Entrada(1, desplazamientos[6], 0);
        Escribir($"6 0 obj\n<< /Type /XRef /Size 7 /W [1 4 2] /Root 1 0 R /Length {entradas.Count} >>\nstream\n");
        pdf.Write(entradas.ToArray());
        Escribir($"\nendstream\nendobj\nstartxref\n{desplazamientos[6]}\n%%EOF\n");

        return pdf.ToArray();
    }

    /// <summary>Rellena con un comentario detrás de %%EOF: mismo PDF válido, con el tamaño exacto pedido.</summary>
    private static byte[] Rellenar(byte[] pdf, long tamano)
    {
        var relleno = new byte[tamano];
        pdf.CopyTo(relleno, 0);
        Array.Fill(relleno, (byte)' ', pdf.Length, relleno.Length - pdf.Length);
        return relleno;
    }

    private static int Paginas(byte[] pdf)
    {
        using var flujo = new MemoryStream(pdf);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);
        return documento.PageCount;
    }

    private static byte[] ConContrasena(byte[] pdf)
    {
        using var entrada = new MemoryStream(pdf);
        using var documento = PdfReader.Open(entrada, PdfDocumentOpenMode.Modify);
        documento.SecuritySettings.UserPassword = "usuario";
        documento.SecuritySettings.OwnerPassword = "propietario";
        using var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        return salida.ToArray();
    }
}
