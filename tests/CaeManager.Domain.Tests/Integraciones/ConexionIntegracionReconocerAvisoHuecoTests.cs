using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

/// <summary>
/// Issue #2124, acción Confirmar: reconocer el aviso del hueco limpia la marca, anula la recuperación en curso, pide su
/// acción de auditoría y no impide que una pérdida posterior vuelva a marcar.
/// </summary>
public class ConexionIntegracionReconocerAvisoHuecoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static ConexionIntegracion ConMarca(DateTime marcaUtc)
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.RegistrarNotificacionDescartada(marcaUtc + ConexionIntegracion.RetrasoMaximoNotificacion);
        conexion.HuecoPendienteDesdeUtc.Should().Be(marcaUtc, "control: la marca de partida");
        return conexion;
    }

    [Fact]
    public void Reconocer_limpia_la_marca_anula_la_recuperacion_y_pide_su_accion_de_auditoria()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-2), hayMas: true);
        conexion.RegistrarAtascoRecuperacionHueco().Should().BeTrue("control: todos los campos de la recuperación tienen valor");
        var auditable = (IAccionAuditoriaPropia)conexion;
        auditable.ConsumirAccionAuditoria().Should().BeNull("control: sin acción propia antes de reconocer");

        conexion.ReconocerAvisoHueco().Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.RecuperacionHuecoId.Should().BeNull();
        conexion.RecuperacionHuecoCubreDesdeUtc.Should().BeNull();
        conexion.RecuperacionHuecoCursorUtc.Should().BeNull();
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        conexion.RecuperacionHuecoAtascadaEnUtc.Should().BeNull();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "no toca el estado");
        auditable.ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionHuecoReconocido);
        auditable.ConsumirAccionAuditoria().Should().BeNull("la marca de auditoría es de un solo uso");
    }

    [Fact]
    public void Una_recuperacion_truncada_y_terminada_tambien_se_reconoce()
    {
        // La marca más antigua que el tope no se limpia sola: Confirmar es la única salida.
        var conexion = ConMarca(Ahora - ConexionIntegracion.AntiguedadMaximaRecuperacion - TimeSpan.FromDays(1));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(null, hayMas: false);
        conexion.CompletarRecuperacionHueco().Should().BeFalse("control: truncada, la marca se queda");

        conexion.ReconocerAvisoHueco().Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
    }

    [Fact]
    public void Sin_marca_no_hay_nada_que_reconocer_ni_que_auditar()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");

        conexion.ReconocerAvisoHueco().Should().BeFalse();

        ((IAccionAuditoriaPropia)conexion).ConsumirAccionAuditoria().Should().BeNull();
    }

    [Fact]
    public void Una_perdida_posterior_vuelve_a_marcar()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.ReconocerAvisoHueco().Should().BeTrue();

        conexion.RegistrarNotificacionDescartada(Ahora.AddMinutes(30)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddMinutes(30) - ConexionIntegracion.RetrasoMaximoNotificacion);
    }

    [Fact]
    public void El_paso_a_ConError_tras_reconocer_vuelve_a_marcar()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.ReconocerAvisoHueco().Should().BeTrue();

        conexion.MarcarConError("token caducado", Ahora.AddMinutes(5));

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddMinutes(5));
    }

    [Fact]
    public void Con_la_conexion_no_Habilitada_o_con_marca_por_registrar_lanza_y_no_toca_nada()
    {
        var deshabilitada = ConMarca(Ahora.AddHours(-3));
        deshabilitada.Deshabilitar();
        var conError = ConMarca(Ahora.AddHours(-3));
        conError.MarcarConError("token caducado", Ahora);
        var porRegistrar = ConMarca(Ahora.AddHours(-3));
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc))!
            .SetValue(porRegistrar, Ahora.AddHours(-4));

        foreach (var conexion in new[] { deshabilitada, conError, porRegistrar })
        {
            var reconocer = () => conexion.ReconocerAvisoHueco();

            reconocer.Should().Throw<InvalidOperationException>();
            conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
            ((IAccionAuditoriaPropia)conexion).ConsumirAccionAuditoria().Should().BeNull();
        }
    }
}
