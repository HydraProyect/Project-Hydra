using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores.Extraccion;

/// <summary>
/// El compositor solo llama a la IA cuando el parser local no cuadra. La IA es un doble que cuenta
/// llamadas: lo que se mide es el envío o no del documento al proveedor, no lo que devuelva.
/// </summary>
public class ExtraccionTrabajadoresConParserLocalServiceTests
{
    private static readonly byte[] Pdf = [1, 2, 3];

    private static readonly TrabajadorExtraidoDto DeLaIa = new("ANA", "RUIZ SANZ", "00000000T");
    private static readonly TrabajadorExtraidoDto DelParser = new("JUAN", "GARCIA LOPEZ", "12345678Z");

    [Fact]
    public async Task Si_el_parser_cuadra_se_usa_su_resultado_y_la_ia_no_recibe_el_documento()
    {
        var ia = new IaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([DeLaIa]));
        var servicio = Crear(new ParserFalso(new ResultadoExtraccionLocal([DelParser], Motivo: null)), ia);

        var resultado = await servicio.ExtraerAsync(Pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Equal(DelParser);
        ia.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_parser_no_cuadra_se_llama_a_la_ia_una_vez_y_se_devuelve_su_resultado()
    {
        var ia = new IaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([DeLaIa]));
        var servicio = Crear(new ParserFalso(ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.IdentificadorInvalido)), ia);

        var resultado = await servicio.ExtraerAsync(Pdf);

        resultado.Valor.Should().Equal(DeLaIa);
        ia.Llamadas.Should().Be(1);
        ia.UltimoContenido.Should().BeSameAs(Pdf);
    }

    [Fact]
    public async Task Un_fallo_de_la_ia_de_respaldo_se_propaga_tal_cual()
    {
        var fallo = Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear("ExtraccionTrabajadores.NoConfigurado", "No disponible."));
        var servicio = Crear(new ParserFalso(ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.SinTexto)), new IaFalsa(fallo));

        var resultado = await servicio.ExtraerAsync(Pdf);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ExtraccionTrabajadores.NoConfigurado");
    }

    [Theory]
    [MemberData(nameof(TodosLosMotivos))]
    public async Task Cada_motivo_de_no_cuadra_manda_el_documento_a_la_ia(MotivoNoCuadra motivo)
    {
        var ia = new IaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([]));
        var servicio = Crear(new ParserFalso(ResultadoExtraccionLocal.NoCuadra(motivo)), ia);

        await servicio.ExtraerAsync(Pdf);

        ia.Llamadas.Should().Be(1, $"«{motivo}» significa que el parser no se fía de su lectura");
    }

    [Fact]
    public async Task Si_el_parser_lanza_una_excepcion_se_lee_con_ia_en_vez_de_dejar_el_documento_sin_lectura()
    {
        var ia = new IaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([DeLaIa]));
        var servicio = Crear(new ParserQueLanza(), ia);

        var resultado = await servicio.ExtraerAsync(Pdf);

        resultado.Valor.Should().Equal(DeLaIa);
        ia.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Un_parser_que_cuadra_con_cero_trabajadores_se_trata_como_no_cuadra_por_el_propio_parser()
    {
        // El contrato: «cuadra» nunca viene con lista vacía (el parser devuelve SinTrabajadores). Se comprueba
        // con el parser real sobre una página con cabecera y sin filas.
        var ia = new IaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([DeLaIa]));
        var parser = TablaSinteticaPdf.ParserSobre(TablaSinteticaPdf.Pagina(1, TablaSinteticaPdf.CabeceraCombinada(750)));

        var resultado = await Crear(parser, ia).ExtraerAsync(Pdf);

        resultado.Valor.Should().Equal(DeLaIa);
        ia.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Con_el_parser_real_una_tabla_que_cuadra_no_llama_a_la_ia_y_una_que_no_cuadra_si()
    {
        var ia = new IaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([DeLaIa]));
        var dni = TablaSinteticaPdf.Dni(12345678);
        var cuadra = TablaSinteticaPdf.ParserSobre(TablaSinteticaPdf.Pagina(1,
            TablaSinteticaPdf.CabeceraCombinada(750),
            TablaSinteticaPdf.FilaCombinada(720, "281234567890", dni, "GARCIA LOPEZ, JUAN")));
        var noCuadra = TablaSinteticaPdf.ParserSobre(TablaSinteticaPdf.Pagina(1,
            TablaSinteticaPdf.CabeceraCombinada(750),
            TablaSinteticaPdf.FilaCombinada(720, "281234567890", TablaSinteticaPdf.ConLetraErronea(dni), "GARCIA LOPEZ, JUAN")));

        var local = await Crear(cuadra, ia).ExtraerAsync(Pdf);
        ia.Llamadas.Should().Be(0);
        local.Valor.Should().ContainSingle().Which.Dni.Should().Be(dni);

        await Crear(noCuadra, ia).ExtraerAsync(Pdf);
        ia.Llamadas.Should().Be(1);
    }

    [Fact]
    public void Anadir_un_motivo_obliga_a_decidir_aqui_que_documento_lo_provoca()
    {
        // Cada motivo tiene su caso en ExtractorTrabajadoresLocalServiceTests; al añadir uno, súbelo aquí y allí.
        Enum.GetValues<MotivoNoCuadra>().Should().HaveCount(13);
    }

    public static IEnumerable<object[]> TodosLosMotivos() =>
        Enum.GetValues<MotivoNoCuadra>().Select(m => new object[] { m });

    private static ExtraccionTrabajadoresConParserLocalService Crear(IExtractorTrabajadoresLocal parser, IExtraccionTrabajadoresIaService ia) =>
        new(parser, ia, NullLogger<ExtraccionTrabajadoresConParserLocalService>.Instance);

    private sealed class ParserFalso(ResultadoExtraccionLocal resultado) : IExtractorTrabajadoresLocal
    {
        public ResultadoExtraccionLocal Extraer(byte[] contenidoPdf) => resultado;
    }

    private sealed class ParserQueLanza : IExtractorTrabajadoresLocal
    {
        public ResultadoExtraccionLocal Extraer(byte[] contenidoPdf) => throw new InvalidOperationException("PDF malformado con texto del documento");
    }

    private sealed class IaFalsa(Result<IReadOnlyList<TrabajadorExtraidoDto>> respuesta) : IExtraccionTrabajadoresIaService
    {
        public int Llamadas { get; private set; }
        public byte[]? UltimoContenido { get; private set; }

        public Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            UltimoContenido = contenidoPdf;
            return Task.FromResult(respuesta);
        }
    }
}
