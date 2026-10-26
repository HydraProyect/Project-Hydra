using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.DocumentosIa;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// El parser local de punta a punta sobre PDF reales (fabricados aquí con PdfPig, con nombres
/// inventados y DNI calculados con su letra de control): PdfPig lee las palabras con su caja, el
/// parser las reparte en filas y el compositor decide si hace falta la IA. Sin documentos ni
/// datos reales.
/// </summary>
public sealed class ParserLocalDeTrabajadoresSobrePdfTests
{
    private const double X_Afiliacion = 40;
    private const double X_Documento = 140;
    private const double X_Nombre = 240;
    private const double X_Alta = 470;

    private static readonly IExtractorTrabajadoresLocal Parser =
        new ExtractorTrabajadoresLocalService(new PdfPigExtractorPalabrasPdf(NullLogger<PdfPigExtractorPalabrasPdf>.Instance));

    private static string Dni(int numero)
    {
        var texto = numero.ToString("D8");
        return texto + ValidadorIdentificacion.LetraControlEsperada(texto)!.Value;
    }

    private static string ConLetraErronea(string identificador) =>
        identificador[..^1] + (identificador[^1] == 'A' ? 'B' : 'A');

    /// <summary>Un ITA sintético: título, cabecera y una fila por Trabajador (documento, nombre).</summary>
    private static byte[] Ita(params (string Documento, string Nombre)[] filas)
    {
        var constructor = new PdfDocumentBuilder();
        var fuente = constructor.AddStandard14Font(Standard14Font.Helvetica);
        var pagina = constructor.AddPage(595, 842);

        pagina.AddText("Informe de trabajadores en alta", 12, new PdfPoint(40, 800), fuente);
        pagina.AddText("NAF", 10, new PdfPoint(X_Afiliacion, 760), fuente);
        pagina.AddText("DNI/NIE", 10, new PdfPoint(X_Documento, 760), fuente);
        pagina.AddText("Apellidos y nombre", 10, new PdfPoint(X_Nombre, 760), fuente);
        pagina.AddText("Fecha alta", 10, new PdfPoint(X_Alta, 760), fuente);

        for (var i = 0; i < filas.Length; i++)
        {
            var y = 730 - (i * 20);
            pagina.AddText($"28123456789{i}", 10, new PdfPoint(X_Afiliacion, y), fuente);
            pagina.AddText(filas[i].Documento, 10, new PdfPoint(X_Documento, y), fuente);
            pagina.AddText(filas[i].Nombre, 10, new PdfPoint(X_Nombre, y), fuente);
            pagina.AddText("01/02/2024", 10, new PdfPoint(X_Alta, y), fuente);
        }

        return constructor.Build();
    }

    [Fact]
    public void Una_tabla_limpia_de_tres_trabajadores_se_lee_en_local()
    {
        var pdf = Ita(
            (Dni(12345678), "GARCIA LOPEZ, JUAN"),
            (Dni(87654321), "PEREZ RUIZ, MARIA DEL CARMEN"),
            (Dni(11111111), "SANZ MORA, PEDRO"));

        var resultado = Parser.Extraer(pdf);

        resultado.Cuadra.Should().BeTrue();
        resultado.Trabajadores.Should().Equal(
            new TrabajadorExtraidoDto("JUAN", "GARCIA LOPEZ", Dni(12345678)),
            new TrabajadorExtraidoDto("MARIA DEL CARMEN", "PEREZ RUIZ", Dni(87654321)),
            new TrabajadorExtraidoDto("PEDRO", "SANZ MORA", Dni(11111111)));
    }

    [Fact]
    public void Un_nie_y_un_dni_repetido_se_leen_y_se_deduplican()
    {
        var nie = "X1234567" + ValidadorIdentificacion.LetraControlEsperada("X1234567")!.Value;
        var pdf = Ita(
            (nie, "KOWALSKI NOWAK, ANNA"),
            (Dni(12345678), "GARCIA LOPEZ, JUAN"),
            (Dni(12345678), "GARCIA LOPEZ, JUAN"));

        var resultado = Parser.Extraer(pdf);

        resultado.Cuadra.Should().BeTrue();
        resultado.Trabajadores.Select(t => t.Dni).Should().Equal(nie, Dni(12345678));
    }

    [Fact]
    public void Un_dni_con_la_letra_erronea_no_cuadra()
    {
        var pdf = Ita(
            (Dni(12345678), "GARCIA LOPEZ, JUAN"),
            (ConLetraErronea(Dni(87654321)), "PEREZ RUIZ, MARIA"));

        var resultado = Parser.Extraer(pdf);

        resultado.Cuadra.Should().BeFalse();
        resultado.Motivo.Should().Be(MotivoNoCuadra.IdentificadorInvalido);
    }

    [Fact]
    public void Un_pdf_sin_texto_no_cuadra()
    {
        var constructor = new PdfDocumentBuilder();
        constructor.AddPage(595, 842);

        Parser.Extraer(constructor.Build()).Motivo.Should().Be(MotivoNoCuadra.SinTexto);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF-1.4 esto no es un pdf completo")]
    public void Un_archivo_que_no_es_un_pdf_legible_no_cuadra(string contenido)
    {
        Parser.Extraer(System.Text.Encoding.UTF8.GetBytes(contenido)).Motivo.Should().Be(MotivoNoCuadra.PdfNoLegible);
    }

    [Fact]
    public async Task El_compositor_con_el_parser_real_no_llama_a_la_ia_si_cuadra_y_si_la_llama_si_no()
    {
        var ia = new IaQueCuenta();
        var servicio = new ExtraccionTrabajadoresConParserLocalService(
            Parser, ia, NullLogger<ExtraccionTrabajadoresConParserLocalService>.Instance);

        var cuadra = await servicio.ExtraerAsync(Ita((Dni(12345678), "GARCIA LOPEZ, JUAN")));
        cuadra.EsExitoso.Should().BeTrue();
        cuadra.Valor.Should().ContainSingle();
        ia.Llamadas.Should().Be(0);

        await servicio.ExtraerAsync(Ita((ConLetraErronea(Dni(12345678)), "GARCIA LOPEZ, JUAN")));
        ia.Llamadas.Should().Be(1);
    }

    private sealed class IaQueCuenta : IExtraccionTrabajadoresIaService
    {
        public int Llamadas { get; private set; }

        public Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([]));
        }
    }
}
