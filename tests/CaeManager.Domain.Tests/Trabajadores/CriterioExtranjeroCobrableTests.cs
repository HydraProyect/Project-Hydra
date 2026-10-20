using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

/// <summary>
/// Criterio de «Trabajador extranjero (cobrable)» de la exportación mensual del Cuadro de dirección (decisión de Chris,
/// 2026-10-20): el NIE no cuenta, solo el pasaporte o un documento no español; la casilla a mano manda.
/// </summary>
public class CriterioExtranjeroCobrableTests
{
    [Theory]
    [InlineData("12345678Z")]      // DNI con letra correcta
    [InlineData("12345678A")]      // DNI con letra mal: sigue siendo DNI
    [InlineData("1234567L")]       // DNI de siete dígitos (Normalizar repone el cero)
    [InlineData("12.345.678-z")]   // DNI con separadores y minúscula
    [InlineData("X1234567L")]      // NIE
    [InlineData("Y 1234567-X")]    // NIE con separadores
    [InlineData("Z1234567A")]      // NIE con letra mal: sigue siendo NIE
    [InlineData("ABC123456")]      // número de soporte de la TIE (formato español)
    public void Un_DNI_un_NIE_o_un_soporte_de_TIE_no_cuentan(string documento) =>
        CriterioExtranjeroCobrable.PorDocumento(documento).Should().BeFalse();

    [Theory]
    [InlineData("AB1234567")]      // pasaporte de dos letras y siete dígitos
    [InlineData("P12345678")]      // pasaporte con prefijo P
    [InlineData("C01X00T47")]      // documento de identidad extranjero alfanumérico
    [InlineData("123456789")]      // pasaporte solo numérico
    public void Un_pasaporte_o_un_documento_no_espanol_cuentan(string documento) =>
        CriterioExtranjeroCobrable.PorDocumento(documento).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  - ")]
    public void Sin_documento_no_cuenta(string? documento) =>
        CriterioExtranjeroCobrable.PorDocumento(documento).Should().BeFalse();

    [Fact]
    public void Sin_marca_decide_el_documento()
    {
        CriterioExtranjeroCobrable.Efectivo(null, "AB1234567").Should().BeTrue();
        CriterioExtranjeroCobrable.Efectivo(null, "X1234567L").Should().BeFalse();
    }

    [Fact]
    public void La_marca_manda_sobre_el_documento_en_los_dos_sentidos()
    {
        CriterioExtranjeroCobrable.Efectivo(false, "AB1234567").Should().BeFalse("p. ej. ciudadano de la UE");
        CriterioExtranjeroCobrable.Efectivo(true, "X1234567L").Should().BeTrue();
        CriterioExtranjeroCobrable.Efectivo(true, "12345678Z").Should().BeTrue();
    }

    [Fact]
    public void La_marca_del_Trabajador_se_fija_se_quita_y_la_anonimizacion_la_vacia()
    {
        var trabajador = Trabajador.DeEmpresa(
            Guid.NewGuid(), "Amina", "El Idrissi", "AB1234567", new DateOnly(1990, 5, 6),
            "amina@ejemplo.test", null, null, null, null);
        trabajador.MarcaExtranjeroCobrable.Should().BeNull();

        trabajador.FijarMarcaExtranjeroCobrable(false);
        trabajador.MarcaExtranjeroCobrable.Should().BeFalse();
        trabajador.FijarMarcaExtranjeroCobrable(null);
        trabajador.MarcaExtranjeroCobrable.Should().BeNull();

        trabajador.FijarMarcaExtranjeroCobrable(true);
        trabajador.Anonimizar(DateTime.UtcNow);
        trabajador.MarcaExtranjeroCobrable.Should().BeNull();

        var fijarTrasAnonimizar = () => trabajador.FijarMarcaExtranjeroCobrable(true);
        fijarTrasAnonimizar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void La_asignacion_a_la_Visita_guarda_cuando_y_quien()
    {
        var gestor = Guid.NewGuid();
        var antes = DateTime.UtcNow;

        var asignacion = new VisitaTrabajador(Guid.NewGuid(), Guid.NewGuid(), gestor);

        asignacion.GestionadoPorUsuarioId.Should().Be(gestor);
        asignacion.FechaAsignacionUtc.Should().NotBeNull().And.BeOnOrAfter(antes);
        asignacion.FechaAsignacionUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void La_asignacion_sin_actor_deja_el_gestor_vacio()
    {
        new VisitaTrabajador(Guid.NewGuid(), Guid.NewGuid()).GestionadoPorUsuarioId.Should().BeNull();
        new VisitaTrabajador(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty).GestionadoPorUsuarioId.Should().BeNull();
    }
}
