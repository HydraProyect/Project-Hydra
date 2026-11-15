using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

/// <summary>
/// D-4 (#2005, opción A + (i)): reabrir el expediente retira el sello entero —fecha, antelaciones, tramo y
/// atribución— para que la siguiente evaluación selle de nuevo con una atribución nueva, y deja constancia del motivo.
/// </summary>
public class VisitaReaperturaExpedienteTests
{
    private static readonly DateTime Solicitud = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sello = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Reapertura = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    private static Visita VisitaSellada()
    {
        var visita = new Visita(Guid.NewGuid(), new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), Solicitud);
        visita.MarcarExpedienteCompleto(Sello,
            new ResultadoAntelacion(30m, 24m, 6m, TramoAntelacion.Urgente, AtribucionUrgencia.SolicitudTardiaCliente))
            .Should().BeTrue("control positivo: la Visita queda sellada");
        return visita;
    }

    [Fact]
    public void Reabrir_retira_el_sello_entero_y_registra_motivo_y_fecha()
    {
        var visita = VisitaSellada();

        visita.ReabrirExpediente(Reapertura, "  El certificado de la mutua era de otra empresa  ");

        visita.FechaHoraExpedienteCompletoUtc.Should().BeNull();
        visita.AntelacionNominalHoras.Should().BeNull();
        visita.AntelacionEfectivaHoras.Should().BeNull();
        visita.Tramo.Should().BeNull();
        visita.Atribucion.Should().Be(AtribucionUrgencia.SinUrgencia);
        visita.ExpedienteReabiertoEnUtc.Should().Be(Reapertura);
        visita.MotivoReaperturaExpediente.Should().Be("El certificado de la mutua era de otra empresa");
        visita.FechaHoraSolicitudUtc.Should().Be(Solicitud, "la solicitud de origen no se toca: el nuevo sello mide desde ella");
    }

    [Fact]
    public void Tras_reabrir_se_puede_volver_a_sellar_con_una_atribucion_nueva()
    {
        var visita = VisitaSellada();
        visita.ReabrirExpediente(Reapertura, "Documento caducado aceptado por error");

        var resellado = visita.MarcarExpedienteCompleto(Reapertura.AddHours(1),
            new ResultadoAntelacion(30m, 10m, 20m, TramoAntelacion.Estandar, AtribucionUrgencia.SinUrgencia));

        resellado.Should().BeTrue();
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Reapertura.AddHours(1));
        visita.Tramo.Should().Be(TramoAntelacion.Estandar);
        visita.AntelacionEfectivaHoras.Should().Be(10m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void El_motivo_es_obligatorio(string? motivo)
    {
        var visita = VisitaSellada();

        var reabrir = () => visita.ReabrirExpediente(Reapertura, motivo!);

        reabrir.Should().Throw<ArgumentException>();
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello, "un intento fallido no retira el sello");
    }

    [Fact]
    public void No_se_reabre_lo_que_no_esta_sellado()
    {
        var visita = new Visita(Guid.NewGuid(), new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), null);

        var reabrir = () => visita.ReabrirExpediente(Reapertura, "motivo");

        reabrir.Should().Throw<InvalidOperationException>();
        visita.ExpedienteReabiertoEnUtc.Should().BeNull();
    }

    [Fact]
    public void No_se_reabre_una_visita_cancelada()
    {
        var visita = VisitaSellada();
        visita.Cancelar(Reapertura.AddHours(-1), "aplazada");

        var reabrir = () => visita.ReabrirExpediente(Reapertura, "motivo");

        reabrir.Should().Throw<InvalidOperationException>();
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
    }
}
