using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// «Pendiente en la plataforma» (decisión del propietario, 2026-10-10): un documento que todavía vale y que en la
/// plataforma CAE de un Centro está sin subir o subido sin validar. Rechazada no es Pendiente (sigue siendo causa propia
/// de Bloqueado, D-7); Validada y No requerida no son trabajo pendiente.
/// </summary>
public class ReglaPendienteEnPlataformaTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, true)]
    [InlineData(EstadoAcreditacion.Subida, true)]
    [InlineData(EstadoAcreditacion.Aceptada, false)]
    [InlineData(EstadoAcreditacion.Rechazada, false)]
    [InlineData(EstadoAcreditacion.NoRequerida, false)]
    public void Solo_sin_subir_y_subido_sin_validar_estan_pendientes(EstadoAcreditacion estado, bool pendiente)
    {
        ReglaPendienteEnPlataforma.EstaPendiente(estado).Should().Be(pendiente);
        ReglaPendienteEnPlataforma.CuentaEnElCentro(estado, EstadoDocumento.Vigente).Should().Be(pendiente);
    }

    [Fact]
    public void El_filtro_para_SQL_sale_de_la_misma_regla()
    {
        ReglaPendienteEnPlataforma.EstadosPendientes.Should().BeEquivalentTo(
            [EstadoAcreditacion.PendienteDeSubir, EstadoAcreditacion.Subida]);
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    [InlineData(EstadoDocumento.SinConfirmar)]
    public void Un_documento_que_vale_cuenta_como_Pendiente_incluido_el_sin_confirmar(EstadoDocumento estadoDocumento)
    {
        // «Sin confirmar» viaja en el paquete de acreditación (decisión del 2026-10-03): sin subir es trabajo pendiente.
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, estadoDocumento).Should().BeTrue();
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.Subida, estadoDocumento).Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.EnTolerancia)]
    [InlineData(EstadoDocumento.Faltante)]
    public void Un_documento_que_ya_no_vale_no_es_Pendiente(EstadoDocumento estadoDocumento)
    {
        // Ya es causa más grave por sí mismo, y renovarlo reinicia la acreditación.
        ReglaPendienteEnPlataforma.CuentaEnElCentro(EstadoAcreditacion.PendienteDeSubir, estadoDocumento).Should().BeFalse();
    }

    [Fact]
    public void Un_par_vigente_pendiente_en_la_plataforma_de_su_Centro_no_esta_al_dia()
    {
        var centro = Guid.NewGuid();
        var alDia = new ParDocumentalExigido(centro, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EstadoDocumento.Vigente);
        var pendiente = alDia with { TrabajadorId = Guid.NewGuid(), PendienteEnPlataforma = true };

        CumplimientoDocumental.EsConforme(alDia).Should().BeTrue();
        CumplimientoDocumental.EsConforme(pendiente).Should().BeFalse();
        CumplimientoDocumental.De(ContextoCumplimiento.Centro, centro, [alDia, pendiente])
            .Should().Be(new FraccionCumplimiento(1, 2), "el pendiente entra en el denominador y no en el numerador");
    }
}
