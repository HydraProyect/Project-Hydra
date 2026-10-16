using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.ValidacionPrevia;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Application.Tests.DocumentosIa.ValidacionPrevia;

public class ValidacionesDeterministasDocumentoTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 16);
    private static readonly DatosFormatoValidacionPrevia PdfValido = new(100, true, true, true, 1);

    [Theory]
    [InlineData(1)]
    [InlineData(LimitesArchivoSubido.TamanoMaximoBytes)]
    public void C0_admite_tamano_positivo_hasta_el_limite(long tamano)
    {
        ValidacionesDeterministasDocumento.EvaluarFormato(PdfValido with { TamanoBytes = tamano })
            .Should().Be(new ResultadoReglaPrevia(ReglaValidacionPrevia.C0, ResultadoReglaValidacionPrevia.Superada));
    }

    [Theory]
    [InlineData(0, true, true, 1, DetalleValidacionPrevia.ArchivoVacio)]
    [InlineData(-1, true, true, 1, DetalleValidacionPrevia.ArchivoVacio)]
    [InlineData(LimitesArchivoSubido.TamanoMaximoBytes + 1, true, true, 1, DetalleValidacionPrevia.ExcedeTamano)]
    [InlineData(100, false, true, 1, DetalleValidacionPrevia.FirmaNoCoincide)]
    [InlineData(100, true, false, 1, DetalleValidacionPrevia.PdfNoAbre)]
    [InlineData(100, true, true, 0, DetalleValidacionPrevia.SinPaginas)]
    [InlineData(100, true, true, -1, DetalleValidacionPrevia.SinPaginas)]
    public void C0_propone_incompleto(long tamano, bool firma, bool abre, int paginas,
        DetalleValidacionPrevia detalle)
    {
        ValidacionesDeterministasDocumento.EvaluarFormato(new(tamano, firma, true, abre, paginas))
            .Should().Be(new ResultadoReglaPrevia(ReglaValidacionPrevia.C0, ResultadoReglaValidacionPrevia.Salta,
                detalle, CausaRechazoAcreditacion.FormatoNoAdmitido, VeredictoPropuestoValidacionPrevia.Incompleto));
    }

    [Fact]
    public void C0_no_exige_apertura_ni_paginas_pdf_a_otro_formato_admitido()
    {
        ValidacionesDeterministasDocumento.EvaluarFormato(new(100, true, false, false, 0))
            .Resultado.Should().Be(ResultadoReglaValidacionPrevia.Superada);
    }

    [Theory]
    [InlineData(0, false, true, false, 0, DetalleValidacionPrevia.ArchivoVacio)]
    [InlineData(LimitesArchivoSubido.TamanoMaximoBytes + 1, false, true, false, 0, DetalleValidacionPrevia.ExcedeTamano)]
    [InlineData(100, false, true, false, 0, DetalleValidacionPrevia.FirmaNoCoincide)]
    [InlineData(100, true, true, false, 0, DetalleValidacionPrevia.PdfNoAbre)]
    public void C0_informa_del_primer_fallo_en_el_orden_publicado(long tamano, bool firma, bool esPdf, bool abre,
        int paginas, DetalleValidacionPrevia esperado)
    {
        ValidacionesDeterministasDocumento.EvaluarFormato(new(tamano, firma, esPdf, abre, paginas))
            .Detalle.Should().Be(esperado);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void C1_admite_hoy_y_manana(int dias)
    {
        ValidacionesDeterministasDocumento.EvaluarFecha(new(Hoy.AddDays(dias), true, Hoy))
            .Should().Be(new ResultadoReglaPrevia(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.Superada));
    }

    [Fact]
    public void C1_propone_invalidar_si_vencio_ayer()
    {
        ValidacionesDeterministasDocumento.EvaluarFecha(new(Hoy.AddDays(-1), true, Hoy))
            .Should().Be(new ResultadoReglaPrevia(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.Salta,
                DetalleValidacionPrevia.VencimientoAnteriorAHoy, CausaRechazoAcreditacion.CaducadoAlPresentar,
                VeredictoPropuestoValidacionPrevia.Invalidar));
    }

    [Fact]
    public void C1_sin_fecha_solo_avisa_si_el_tipo_caduca()
    {
        ValidacionesDeterministasDocumento.EvaluarFecha(new(null, true, Hoy))
            .Should().Be(new ResultadoReglaPrevia(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.Aviso,
                DetalleValidacionPrevia.VencimientoAusenteEnTipoQueCaduca));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void C1_no_se_evalua_si_el_tipo_no_caduca(int? dias)
    {
        ValidacionesDeterministasDocumento.EvaluarFecha(new(dias is { } d ? Hoy.AddDays(d) : null, false, Hoy))
            .Should().Be(new ResultadoReglaPrevia(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.NoEvaluable));
    }

    [Theory]
    [InlineData(0, -1, ResultadoReglaValidacionPrevia.Salta, ResultadoReglaValidacionPrevia.Salta)]
    [InlineData(0, 0, ResultadoReglaValidacionPrevia.Salta, ResultadoReglaValidacionPrevia.Superada)]
    [InlineData(100, -1, ResultadoReglaValidacionPrevia.Superada, ResultadoReglaValidacionPrevia.Salta)]
    [InlineData(100, 0, ResultadoReglaValidacionPrevia.Superada, ResultadoReglaValidacionPrevia.Superada)]
    public void Devuelve_ambas_reglas_independientemente(int tamano, int dias,
        ResultadoReglaValidacionPrevia c0, ResultadoReglaValidacionPrevia c1)
    {
        var resultados = ValidacionesDeterministasDocumento.Evaluar(
            PdfValido with { TamanoBytes = tamano }, new(Hoy.AddDays(dias), true, Hoy));

        resultados.Select(r => r.Regla).Should().Equal(ReglaValidacionPrevia.C0, ReglaValidacionPrevia.C1);
        resultados.Select(r => r.Resultado).Should().Equal(c0, c1);
    }
}
