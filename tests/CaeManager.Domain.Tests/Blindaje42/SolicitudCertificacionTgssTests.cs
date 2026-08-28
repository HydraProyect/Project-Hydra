using CaeManager.Domain.Blindaje42;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Blindaje42;

public class SolicitudCertificacionTgssTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private static SolicitudCertificacionTgss Crear(
        DateOnly? fechaSolicitud = null, string? observaciones = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), fechaSolicitud ?? Hoy, Guid.NewGuid(), observaciones);

    [Fact]
    public void Crea_una_solicitud_valida()
    {
        var solicitud = Crear(observaciones: "  Enviada por burofax.  ");

        solicitud.FechaSolicitud.Should().Be(Hoy);
        solicitud.Observaciones.Should().Be("Enviada por burofax.");
        solicitud.Resultado.Should().BeNull();
        solicitud.FechaLimiteOrientativa.Should().Be(Hoy.AddDays(SolicitudCertificacionTgss.PlazoDiasTgss));
    }

    [Fact]
    public void Rechaza_una_fecha_de_solicitud_futura()
    {
        var accion = () => Crear(fechaSolicitud: Hoy.AddDays(1));

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("empresa")]
    [InlineData("cliente")]
    [InlineData("usuario")]
    public void Exige_todas_las_referencias(string faltante)
    {
        var accion = () => new SolicitudCertificacionTgss(
            faltante == "empresa" ? Guid.Empty : Guid.NewGuid(),
            faltante == "cliente" ? Guid.Empty : Guid.NewGuid(),
            Hoy,
            faltante == "usuario" ? Guid.Empty : Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_observaciones_demasiado_largas()
    {
        var accion = () => Crear(observaciones: new string('x', SolicitudCertificacionTgss.LongitudMaximaObservaciones + 1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Registra_una_respuesta()
    {
        var solicitud = Crear();
        var usuarioId = Guid.NewGuid();

        solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.SinDescubiertos, Hoy, usuarioId);

        solicitud.Resultado.Should().Be(ResultadoCertificacionTgss.SinDescubiertos);
        solicitud.FechaRespuesta.Should().Be(Hoy);
        solicitud.RespuestaRegistradaPorUsuarioId.Should().Be(usuarioId);
    }

    [Fact]
    public void Rechaza_una_segunda_respuesta_sobre_la_misma_solicitud()
    {
        var solicitud = Crear();
        solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.SinDescubiertos, Hoy, Guid.NewGuid());

        var accion = () => solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.ConDescubiertos, Hoy, Guid.NewGuid());

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Rechaza_una_respuesta_anterior_a_la_solicitud()
    {
        var solicitud = Crear(fechaSolicitud: Hoy);

        var accion = () => solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.SinDescubiertos, Hoy.AddDays(-1), Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_respuesta_futura()
    {
        var solicitud = Crear();

        var accion = () => solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.SinDescubiertos, Hoy.AddDays(1), Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Adjunta_evidencia_con_ruta_y_nombre()
    {
        var solicitud = Crear();

        solicitud.AdjuntarEvidencia("carpeta/archivo-opaco", "certificado-tgss.pdf");

        solicitud.EvidenciaArchivoRuta.Should().Be("carpeta/archivo-opaco");
        solicitud.EvidenciaNombreArchivo.Should().Be("certificado-tgss.pdf");
    }

    [Fact]
    public void Rechaza_evidencia_sin_ruta_o_sin_nombre()
    {
        var solicitud = Crear();

        var sinRuta = () => solicitud.AdjuntarEvidencia("  ", "certificado.pdf");
        var sinNombre = () => solicitud.AdjuntarEvidencia("carpeta/archivo", "  ");

        sinRuta.Should().Throw<ArgumentException>();
        sinNombre.Should().Throw<ArgumentException>();
    }
}
