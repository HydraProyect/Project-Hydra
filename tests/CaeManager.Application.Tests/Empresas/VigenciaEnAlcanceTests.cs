using CaeManager.Application.Empresas.Queries.ObtenerVigenciaEmpresa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>La regla de cada celda de la matriz «Vigencia» de Empresa 360 según el alcance de Centros.</summary>
public class VigenciaEnAlcanceTests
{
    private static readonly DateOnly Hoy = new(2026, 8, 18);
    private static readonly Guid Zaragoza = Guid.NewGuid();
    private static readonly Guid Berriz = Guid.NewGuid();

    private static VigenciaColumnaDto Columna(int? zaragoza, int? berriz) => new(
        Guid.NewGuid(), "Altura", null, GrupoColumnaVigencia.Formacion, 60,
        [.. new[] { zaragoza is null ? null : new VigenciaExigenciaCentroDto(Zaragoza, zaragoza), berriz is null ? null : new VigenciaExigenciaCentroDto(Berriz, berriz) }.OfType<VigenciaExigenciaCentroDto>()]);

    private static VigenciaCeldaDto Documento(DateOnly vencimiento, DateOnly emision, EstadoCeldaVigencia estado = EstadoCeldaVigencia.Vigente, IReadOnlyList<Guid>? exigidaPor = null) =>
        new(estado, Guid.NewGuid(), vencimiento, emision, exigidaPor ?? []);

    [Fact]
    public void Sin_documento_exigido_es_Falta_y_sin_exigencia_es_No_aplica()
    {
        var columna = Columna(60, null);

        VigenciaEnAlcance.Resolver(columna, new VigenciaCeldaDto(EstadoCeldaVigencia.Falta, null, null, null, [Zaragoza]), null, Hoy, 30)
            .Should().Be(new VigenciaEnAlcance.Resultado(EstadoCeldaVigencia.Falta, null, true, 60));
        VigenciaEnAlcance.Resolver(columna, new VigenciaCeldaDto(EstadoCeldaVigencia.NoAplica, null, null, null, []), null, Hoy, 30)
            .Estado.Should().Be(EstadoCeldaVigencia.NoAplica);
    }

    [Theory]
    [InlineData(-1, EstadoCeldaVigencia.Vencido)]
    [InlineData(0, EstadoCeldaVigencia.PorVencer)]
    [InlineData(30, EstadoCeldaVigencia.PorVencer)]
    [InlineData(31, EstadoCeldaVigencia.Vigente)]
    public void El_umbral_de_por_vencer_es_inclusivo_y_hoy_todavia_vale(int dias, EstadoCeldaVigencia esperado)
    {
        var vencimiento = Hoy.AddDays(dias);
        var celda = Documento(vencimiento, vencimiento.AddMonths(-60), exigidaPor: [Zaragoza]);

        VigenciaEnAlcance.Resolver(Columna(60, null), celda, null, Hoy, 30).Estado.Should().Be(esperado);
    }

    [Fact]
    public void Manda_el_Centro_mas_exigente_del_alcance_y_el_filtro_de_Centro_cambia_la_fecha()
    {
        // Emitido hace 36 meses con vigencia propia de 60: Berriz pide 36 meses (vence hoy), Zaragoza 60.
        var emision = Hoy.AddMonths(-36);
        var celda = Documento(emision.AddMonths(60), emision, exigidaPor: [Zaragoza, Berriz]);
        var columna = Columna(60, 36);

        var todos = VigenciaEnAlcance.Resolver(columna, celda, null, Hoy, 30);
        todos.Fecha.Should().Be(Hoy);
        todos.Estado.Should().Be(EstadoCeldaVigencia.PorVencer);
        todos.VigenciaMinimaMeses.Should().Be(36);

        var soloZaragoza = VigenciaEnAlcance.Resolver(columna, celda, [Zaragoza], Hoy, 30);
        soloZaragoza.Fecha.Should().Be(emision.AddMonths(60));
        soloZaragoza.Estado.Should().Be(EstadoCeldaVigencia.Vigente);
        soloZaragoza.VigenciaMinimaMeses.Should().Be(60);
    }

    [Fact]
    public void Nunca_se_alarga_la_caducidad_propia_del_documento()
    {
        // El Centro pide 60 meses, pero el documento caduca antes: vale la fecha del documento.
        var emision = Hoy.AddMonths(-12);
        var celda = Documento(Hoy.AddDays(-5), emision, exigidaPor: [Zaragoza]);

        var resultado = VigenciaEnAlcance.Resolver(Columna(60, null), celda, null, Hoy, 30);

        resultado.Fecha.Should().Be(Hoy.AddDays(-5));
        resultado.Estado.Should().Be(EstadoCeldaVigencia.Vencido);
    }

    [Fact]
    public void Un_documento_que_ningun_Centro_del_alcance_exige_es_opcional_con_su_propia_fecha()
    {
        var celda = Documento(Hoy.AddDays(-400), Hoy.AddDays(-800), exigidaPor: [Berriz]);

        var resultado = VigenciaEnAlcance.Resolver(Columna(60, 36), celda, [Zaragoza], Hoy, 30);

        resultado.Exigida.Should().BeFalse();
        resultado.Estado.Should().Be(EstadoCeldaVigencia.Vigente, "lo opcional vencido no es problema: no se exige");
        resultado.Fecha.Should().Be(Hoy.AddDays(-400));
    }

    [Fact]
    public void Sin_confirmar_y_sin_caducidad_se_respetan()
    {
        var sinConfirmar = Documento(Hoy.AddDays(-9), Hoy.AddDays(-99), EstadoCeldaVigencia.SinConfirmar, [Zaragoza]);
        VigenciaEnAlcance.Resolver(Columna(60, null), sinConfirmar, null, Hoy, 30).Estado.Should().Be(EstadoCeldaVigencia.SinConfirmar);

        var noCaduca = new VigenciaCeldaDto(EstadoCeldaVigencia.Vigente, Guid.NewGuid(), null, Hoy.AddYears(-3), [Zaragoza]);
        var resultado = VigenciaEnAlcance.Resolver(Columna(60, null), noCaduca, null, Hoy, 30);
        resultado.Estado.Should().Be(EstadoCeldaVigencia.Vigente);
        resultado.Fecha.Should().BeNull();
    }
}
