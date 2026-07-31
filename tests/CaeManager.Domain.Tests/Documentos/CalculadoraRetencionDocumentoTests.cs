using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Regla de retención de Documentos (RGPD-TRATAMIENTO-DATOS.md § 5).
///
/// El criterio predeterminado —contar desde el evento más temprano— lo decidió
/// el propietario del producto con este razonamiento: un documento subido el
/// último día de su vigencia hay que conservarlo igualmente su plazo desde
/// ahí, y contar desde la emisión haría que dos documentos idénticos se
/// purgaran en momentos distintos según cuándo se hubieran subido.
///
/// Estos tests fijan esa decisión. Si alguien cambia el criterio por defecto,
/// falla aquí y no en producción cinco años después.
/// </summary>
public class CalculadoraRetencionDocumentoTests
{
    private const int CincoAnios = 5;

    private static readonly DateOnly Emision = new(2026, 1, 15);
    private static readonly DateOnly Vencimiento = new(2027, 1, 15);

    [Fact]
    public void Un_documento_vigente_cuenta_desde_su_vencimiento_no_desde_su_emision()
    {
        var inicio = CalculadoraRetencionDocumento.CalcularInicioRetencion(
            Emision, Vencimiento, fechaCeseRelacion: null, CriterioInicioRetencion.EventoMasTemprano);

        inicio.Should().Be(Vencimiento);
    }

    [Fact]
    public void Un_documento_sin_vencimiento_cuenta_desde_la_emision()
    {
        // Es lo que evita los documentos perpetuos: sin fecha de caducidad no
        // habría ningún evento del que partir.
        var inicio = CalculadoraRetencionDocumento.CalcularInicioRetencion(
            Emision, fechaVencimiento: null, fechaCeseRelacion: null, CriterioInicioRetencion.EventoMasTemprano);

        inicio.Should().Be(Emision);
    }

    [Fact]
    public void Con_el_cese_anterior_al_vencimiento_manda_el_cese()
    {
        var cese = new DateOnly(2026, 6, 1);

        var inicio = CalculadoraRetencionDocumento.CalcularInicioRetencion(
            Emision, Vencimiento, cese, CriterioInicioRetencion.EventoMasTemprano);

        inicio.Should().Be(cese);
    }

    [Fact]
    public void Con_el_vencimiento_anterior_al_cese_manda_el_vencimiento()
    {
        // El caso que motiva el criterio: el trabajador sigue de alta pero el
        // documento caducó — su plazo arranca ahí.
        var cese = new DateOnly(2030, 3, 1);

        var inicio = CalculadoraRetencionDocumento.CalcularInicioRetencion(
            Emision, Vencimiento, cese, CriterioInicioRetencion.EventoMasTemprano);

        inicio.Should().Be(Vencimiento);
    }

    [Fact]
    public void La_fecha_de_purga_es_el_inicio_mas_el_plazo()
    {
        var fechaPurga = CalculadoraRetencionDocumento.CalcularFechaPurga(
            Emision, Vencimiento, fechaCeseRelacion: null, CincoAnios, CriterioInicioRetencion.EventoMasTemprano);

        fechaPurga.Should().Be(new DateOnly(2032, 1, 15));
    }

    [Fact]
    public void Dos_documentos_iguales_se_purgan_a_la_vez_aunque_se_subieran_en_fechas_distintas()
    {
        // La razón de ser del criterio: la fecha de purga no depende de
        // cuándo se subió el documento, solo de sus propias fechas.
        var primero = CalculadoraRetencionDocumento.CalcularFechaPurga(
            Emision, Vencimiento, null, CincoAnios, CriterioInicioRetencion.EventoMasTemprano);
        var segundo = CalculadoraRetencionDocumento.CalcularFechaPurga(
            Emision, Vencimiento, null, CincoAnios, CriterioInicioRetencion.EventoMasTemprano);

        primero.Should().Be(segundo);
    }

    [Theory]
    [InlineData(2031, 12, 31, false)]
    [InlineData(2032, 1, 14, false)]
    [InlineData(2032, 1, 15, true)]
    [InlineData(2033, 1, 1, true)]
    public void Solo_se_puede_purgar_cumplido_el_plazo(int anio, int mes, int dia, bool esperado)
    {
        var puede = CalculadoraRetencionDocumento.PuedePurgarse(
            Emision, Vencimiento, null, new DateOnly(anio, mes, dia), CincoAnios,
            CriterioInicioRetencion.EventoMasTemprano);

        puede.Should().Be(esperado);
    }

    [Fact]
    public void Con_el_criterio_conservador_y_la_relacion_abierta_no_se_purga()
    {
        // EventoMasTardio necesita los dos eventos: mientras la relación siga
        // viva no hay fecha, y ante la duda se conserva.
        var inicio = CalculadoraRetencionDocumento.CalcularInicioRetencion(
            Emision, Vencimiento, fechaCeseRelacion: null, CriterioInicioRetencion.EventoMasTardio);

        inicio.Should().BeNull();

        CalculadoraRetencionDocumento.PuedePurgarse(
            Emision, Vencimiento, null, new DateOnly(2099, 1, 1), CincoAnios, CriterioInicioRetencion.EventoMasTardio)
            .Should().BeFalse();
    }

    [Fact]
    public void Con_el_criterio_conservador_manda_el_evento_posterior()
    {
        var cese = new DateOnly(2030, 3, 1);

        var inicio = CalculadoraRetencionDocumento.CalcularInicioRetencion(
            Emision, Vencimiento, cese, CriterioInicioRetencion.EventoMasTardio);

        inicio.Should().Be(cese);
    }

    [Fact]
    public void Un_trabajador_de_baja_cuenta_cinco_anios_desde_la_baja()
    {
        var baja = new DateOnly(2026, 3, 10);

        var fechaPurga = CalculadoraRetencionDocumento.CalcularFechaPurgaTrabajador(baja, CincoAnios);

        fechaPurga.Should().Be(new DateOnly(2031, 3, 10));
    }

    [Fact]
    public void Un_trabajador_de_alta_no_tiene_fecha_de_purga()
    {
        // Mientras dure la relación se conserva: no hay hito del que contar.
        CalculadoraRetencionDocumento.CalcularFechaPurgaTrabajador(fechaBaja: null, CincoAnios)
            .Should().BeNull();

        CalculadoraRetencionDocumento.PuedePurgarseTrabajador(null, new DateOnly(2099, 1, 1), CincoAnios)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(2031, 3, 9, false)]
    [InlineData(2031, 3, 10, true)]
    public void El_trabajador_solo_se_purga_cumplido_el_plazo(int anio, int mes, int dia, bool esperado)
    {
        var baja = new DateOnly(2026, 3, 10);

        CalculadoraRetencionDocumento.PuedePurgarseTrabajador(baja, new DateOnly(anio, mes, dia), CincoAnios)
            .Should().Be(esperado);
    }

    [Fact]
    public void Un_plazo_de_retencion_no_positivo_es_un_error_de_configuracion()
    {
        // Un cero aquí purgaría todo el histórico de golpe: mejor que
        // reviente al configurarlo que descubrirlo con los datos borrados.
        var calcular = () => CalculadoraRetencionDocumento.CalcularFechaPurga(
            Emision, Vencimiento, null, aniosRetencion: 0, CriterioInicioRetencion.EventoMasTemprano);

        calcular.Should().Throw<ArgumentOutOfRangeException>();
    }
}
