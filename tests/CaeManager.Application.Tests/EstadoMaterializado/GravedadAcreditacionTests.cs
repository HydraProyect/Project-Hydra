using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.EstadoMaterializado;

/// <summary>
/// Qué acreditación representa al par en el estado materializado: la más grave. Una No requerida es la menos grave aunque
/// conserve una fecha pasada, como las filas anteriores a la invariante «No requerida implica vigencia Sin confirmar».
/// </summary>
public class GravedadAcreditacionTests
{
    private static readonly DateOnly Hoy = new(2026, 11, 11);

    private static FilaEstadoEnPlataforma Fila(EstadoAcreditacion estado, EstadoVigenciaEnPlataforma vigencia, DateOnly? fecha) =>
        new(Guid.NewGuid(), Guid.NewGuid(), AmbitoAplicacion.Trabajador, Guid.NewGuid(), Guid.NewGuid(), estado, vigencia, fecha, null);

    [Fact]
    public void Una_no_requerida_historica_con_fecha_pasada_es_la_menos_grave_y_no_oculta_a_una_pendiente()
    {
        var historica = Fila(EstadoAcreditacion.NoRequerida, EstadoVigenciaEnPlataforma.VenceEnFecha, Hoy.AddDays(-30));
        var pendiente = Fila(EstadoAcreditacion.PendienteDeSubir, EstadoVigenciaEnPlataforma.SinConfirmar, null);

        RecalculoEstadoMaterializadoService.GravedadAcreditacion(historica, Hoy).Should().Be(5);
        RecalculoEstadoMaterializadoService.GravedadAcreditacion(historica, Hoy).Should()
            .BeGreaterThan(RecalculoEstadoMaterializadoService.GravedadAcreditacion(pendiente, Hoy));
    }

    [Fact]
    public void Una_validada_con_fecha_pasada_sigue_siendo_vencida_en_la_plataforma()
    {
        // Control positivo: la excepción es solo para No requerida.
        var vencida = Fila(EstadoAcreditacion.Aceptada, EstadoVigenciaEnPlataforma.VenceEnFecha, Hoy.AddDays(-30));

        RecalculoEstadoMaterializadoService.GravedadAcreditacion(vencida, Hoy).Should().Be(1);
    }
}
