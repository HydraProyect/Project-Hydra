using CaeManager.Application.Comunicaciones.Deteccion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Decisión pura de FiltrarTrabajadoresFueraDeVentana (ronda de reducción de ruido en
/// Comunicaciones, patrón "trabajador fuera de ventana") — un ítem cuyo Trabajador resuelto ya no
/// tiene asignación activa se descarta antes de crear ninguna sugerencia, no hay ninguna gestión
/// real que el gestor pueda hacer sobre un Trabajador dado de baja.
/// </summary>
public class SugerenciaGestionCorreoServiceTests
{
    [Fact]
    public void Un_item_con_trabajador_activo_se_conserva()
    {
        var trabajadorId = Guid.NewGuid();
        var items = new[] { new ItemDeteccionGestionDto(trabajadorId, Guid.NewGuid(), 90, 90) };

        var resultado = SugerenciaGestionCorreoService.FiltrarTrabajadoresFueraDeVentana(items, new HashSet<Guid> { trabajadorId });

        resultado.Should().ContainSingle();
    }

    [Fact]
    public void Un_item_con_trabajador_sin_asignacion_activa_se_descarta()
    {
        var trabajadorId = Guid.NewGuid();
        var items = new[] { new ItemDeteccionGestionDto(trabajadorId, Guid.NewGuid(), 90, 90) };

        var resultado = SugerenciaGestionCorreoService.FiltrarTrabajadoresFueraDeVentana(items, new HashSet<Guid>());

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Un_item_sin_trabajador_resuelto_nunca_se_descarta_por_este_motivo()
    {
        var items = new[] { new ItemDeteccionGestionDto(null, Guid.NewGuid(), 90, 90) };

        var resultado = SugerenciaGestionCorreoService.FiltrarTrabajadoresFueraDeVentana(items, new HashSet<Guid>());

        resultado.Should().ContainSingle();
    }

    [Fact]
    public void Una_notificacion_en_bloque_conserva_solo_los_trabajadores_activos()
    {
        var trabajadorActivo = Guid.NewGuid();
        var trabajadorInactivo = Guid.NewGuid();
        var tipoDocumentoId = Guid.NewGuid();
        var items = new[]
        {
            new ItemDeteccionGestionDto(trabajadorActivo, tipoDocumentoId, 90, 90),
            new ItemDeteccionGestionDto(trabajadorInactivo, tipoDocumentoId, 90, 90),
        };

        var resultado = SugerenciaGestionCorreoService.FiltrarTrabajadoresFueraDeVentana(items, new HashSet<Guid> { trabajadorActivo });

        resultado.Should().ContainSingle();
        resultado[0].TrabajadorId.Should().Be(trabajadorActivo);
    }
}
