using CaeManager.Domain.Retencion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Retencion;

/// <summary>
/// La garantía que sostiene todo el flujo de retención: <b>nada se destruye
/// sin que una persona lo autorice expresamente</b> (decisión del propietario
/// del producto, 2026-07-31 — antes de destruir hay que poder avisar al tenant
/// para que extraiga sus datos si su política interna exige más años).
///
/// Estos tests existen para que esa garantía no se pueda perder por descuido:
/// si alguien añade un atajo de "detectada" a "ejecutada", fallan.
/// </summary>
public class SolicitudPurgaTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 31);
    private static readonly Guid Autorizador = Guid.NewGuid();

    private static SolicitudPurga CrearSolicitud() =>
        new(TipoDatoPurgable.Documentos, registrosAfectados: 120, fechaCorte: new DateOnly(2026, 1, 1));

    [Fact]
    public void Nace_pendiente_de_revision_y_sin_nada_autorizado()
    {
        var solicitud = CrearSolicitud();

        solicitud.Estado.Should().Be(EstadoSolicitudPurga.PendienteDeRevision);
        solicitud.FechaEjecucionProgramada.Should().BeNull();
        solicitud.AutorizadaPorUsuarioId.Should().BeNull();
        solicitud.PuedeEjecutarse(Hoy).Should().BeFalse();
    }

    [Fact]
    public void Una_solicitud_recien_detectada_no_se_puede_ejecutar()
    {
        // El corazón del requisito: vencer el plazo no basta para destruir.
        var solicitud = CrearSolicitud();

        var ejecutar = () => solicitud.Ejecutar(Hoy);

        ejecutar.Should().Throw<InvalidOperationException>()
            .WithMessage("*previamente autorizada y programada*");
    }

    [Fact]
    public void Programar_exige_quien_autoriza()
    {
        var solicitud = CrearSolicitud();

        var programar = () => solicitud.Programar(Hoy.AddDays(30), Guid.Empty, Hoy);

        programar.Should().Throw<ArgumentException>().WithMessage("*quien la autoriza*");
    }

    [Fact]
    public void No_se_puede_programar_para_una_fecha_pasada()
    {
        // Programar "para ayer" sería ejecutar en el acto, que es justo lo
        // que este flujo evita.
        var solicitud = CrearSolicitud();

        var programar = () => solicitud.Programar(Hoy.AddDays(-1), Autorizador, Hoy);

        programar.Should().Throw<ArgumentException>().WithMessage("*anterior a hoy*");
    }

    [Fact]
    public void Programada_pero_antes_de_la_fecha_sigue_sin_ejecutarse()
    {
        var solicitud = CrearSolicitud();
        solicitud.Programar(Hoy.AddDays(30), Autorizador, Hoy);

        solicitud.PuedeEjecutarse(Hoy).Should().BeFalse();

        var ejecutar = () => solicitud.Ejecutar(Hoy);
        ejecutar.Should().Throw<InvalidOperationException>().WithMessage("*no ha llegado la fecha*");
    }

    [Fact]
    public void Llegada_la_fecha_y_autorizada_se_ejecuta()
    {
        var solicitud = CrearSolicitud();
        var fechaEjecucion = Hoy.AddDays(30);
        solicitud.Programar(fechaEjecucion, Autorizador, Hoy);

        solicitud.PuedeEjecutarse(fechaEjecucion).Should().BeTrue();
        solicitud.Ejecutar(fechaEjecucion);

        solicitud.Estado.Should().Be(EstadoSolicitudPurga.Ejecutada);
        solicitud.EjecutadaEnUtc.Should().NotBeNull();
        solicitud.AutorizadaPorUsuarioId.Should().Be(Autorizador);
    }

    [Fact]
    public void El_aviso_al_tenant_queda_registrado()
    {
        // Es la prueba de que el tenant tuvo ocasión de extraer sus datos:
        // el motivo por el que este flujo no es automático.
        var solicitud = CrearSolicitud();

        solicitud.MarcarTenantAvisado();

        solicitud.Estado.Should().Be(EstadoSolicitudPurga.TenantAvisado);
        solicitud.AvisadaAlTenantEnUtc.Should().NotBeNull();
    }

    [Fact]
    public void Una_solicitud_ya_programada_se_puede_cancelar()
    {
        // El caso real: el tenant pide más plazo después de que se autorizara.
        var solicitud = CrearSolicitud();
        solicitud.Programar(Hoy.AddDays(30), Autorizador, Hoy);

        solicitud.Cancelar("El tenant conserva estos documentos 10 años por política interna.");

        solicitud.Estado.Should().Be(EstadoSolicitudPurga.Cancelada);
        solicitud.PuedeEjecutarse(Hoy.AddDays(60)).Should().BeFalse();
    }

    [Fact]
    public void Cancelar_exige_dejar_dicho_por_que()
    {
        var solicitud = CrearSolicitud();

        var cancelar = () => solicitud.Cancelar("  ");

        cancelar.Should().Throw<ArgumentException>().WithMessage("*por qué*");
    }

    [Fact]
    public void Una_purga_ejecutada_no_admite_marcha_atras()
    {
        var solicitud = CrearSolicitud();
        var fecha = Hoy.AddDays(10);
        solicitud.Programar(fecha, Autorizador, Hoy);
        solicitud.Ejecutar(fecha);

        solicitud.Invoking(s => s.Cancelar("tarde")).Should().Throw<InvalidOperationException>();
        solicitud.Invoking(s => s.Programar(fecha.AddDays(1), Autorizador, fecha)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_solicitud_cancelada_no_se_reactiva_programandola()
    {
        var solicitud = CrearSolicitud();
        solicitud.Cancelar("El tenant pidió conservarlos.");

        var programar = () => solicitud.Programar(Hoy.AddDays(5), Autorizador, Hoy);

        programar.Should().Throw<InvalidOperationException>().WithMessage("*no admite cambios*");
    }

    [Fact]
    public void No_tiene_sentido_una_solicitud_sin_registros()
    {
        var crear = () => new SolicitudPurga(TipoDatoPurgable.TrabajadoresDadosDeBaja, 0, Hoy);

        crear.Should().Throw<ArgumentOutOfRangeException>();
    }
}
