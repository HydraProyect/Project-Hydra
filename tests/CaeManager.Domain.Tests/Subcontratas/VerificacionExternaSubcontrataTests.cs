using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Subcontratas;

public class VerificacionExternaSubcontrataTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private static VerificacionExternaSubcontrata Crear(
        DateOnly? fecha = null, ResultadoVerificacionExterna resultado = ResultadoVerificacionExterna.Valido,
        DateOnly? validoHasta = null, string? observaciones = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fecha ?? Hoy, resultado, Guid.NewGuid(), validoHasta, observaciones);

    [Fact]
    public void Crea_una_verificacion_valida()
    {
        var verificacion = Crear(validoHasta: Hoy.AddDays(90), observaciones: "  Consta como aceptado.  ");

        verificacion.Resultado.Should().Be(ResultadoVerificacionExterna.Valido);
        verificacion.ValidoHasta.Should().Be(Hoy.AddDays(90));
        verificacion.Observaciones.Should().Be("Consta como aceptado.");
        verificacion.EvidenciaArchivoRuta.Should().BeNull();
    }

    [Fact]
    public void Rechaza_una_fecha_de_verificacion_futura()
    {
        var accion = () => Crear(fecha: Hoy.AddDays(1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Permite_un_valido_hasta_ya_vencido_porque_es_un_hallazgo_real()
    {
        var verificacion = Crear(validoHasta: Hoy.AddDays(-30));

        verificacion.ValidoHasta.Should().Be(Hoy.AddDays(-30));
    }

    [Theory]
    [InlineData("subcontrata")]
    [InlineData("centro")]
    [InlineData("tipo")]
    [InlineData("usuario")]
    public void Exige_todas_las_referencias(string faltante)
    {
        var accion = () => new VerificacionExternaSubcontrata(
            faltante == "subcontrata" ? Guid.Empty : Guid.NewGuid(),
            faltante == "centro" ? Guid.Empty : Guid.NewGuid(),
            faltante == "tipo" ? Guid.Empty : Guid.NewGuid(),
            Hoy, ResultadoVerificacionExterna.Valido,
            faltante == "usuario" ? Guid.Empty : Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_observaciones_demasiado_largas()
    {
        var accion = () => Crear(observaciones: new string('x', VerificacionExternaSubcontrata.LongitudMaximaObservaciones + 1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Adjunta_evidencia_con_ruta_y_nombre()
    {
        var verificacion = Crear();

        verificacion.AdjuntarEvidencia("carpeta/archivo-opaco", "captura-twind.png");

        verificacion.EvidenciaArchivoRuta.Should().Be("carpeta/archivo-opaco");
        verificacion.EvidenciaNombreArchivo.Should().Be("captura-twind.png");
    }

    [Fact]
    public void Rechaza_evidencia_sin_ruta_o_sin_nombre()
    {
        var verificacion = Crear();

        var sinRuta = () => verificacion.AdjuntarEvidencia("  ", "captura.png");
        var sinNombre = () => verificacion.AdjuntarEvidencia("carpeta/archivo", "  ");

        sinRuta.Should().Throw<ArgumentException>();
        sinNombre.Should().Throw<ArgumentException>();
    }
}
