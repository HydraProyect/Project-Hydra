using CaeManager.Domain.Asignaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Asignaciones;

/// <summary>
/// DEC-19 (REC-064): el límite del rango [FechaAlta, FechaBaja) que decide si
/// dos vigencias del mismo trío se solapan. Complementa
/// SolapamientoDeAsignacionesTests (IntegrationTests, que prueba el rechazo de
/// extremo a extremo) con la tabla de verdad del propio predicado.
/// </summary>
public class AsignacionSeSolapaConTests
{
    private static Asignacion CrearAsignacion(DateOnly fechaAlta, DateOnly? fechaBaja = null)
    {
        var asignacion = new Asignacion(Guid.NewGuid(), Guid.NewGuid(), fechaAlta);
        if (fechaBaja is not null) asignacion.DarDeBaja(fechaBaja.Value);
        return asignacion;
    }

    [Fact]
    public void Detecta_el_solape_cuando_la_otra_alta_cae_dentro_del_rango_ya_cerrado()
    {
        // El caso exacto de SolapamientoDeAsignacionesTests: A cerrada
        // [2026-01-01, 2026-06-01), B abierta desde 2026-03-01 (dentro de A).
        var a = CrearAsignacion(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1));

        a.SeSolapaCon(new DateOnly(2026, 3, 1), null).Should().BeTrue();
    }

    [Fact]
    public void No_hay_solape_al_dar_de_baja_y_reasignar_el_mismo_dia()
    {
        // ReasignarMismoDiaTests: el límite superior es EXCLUSIVO — cerrar
        // hoy y volver a abrir hoy mismo no puede volver a lanzar 23505.
        var hoy = new DateOnly(2026, 5, 10);
        var cerrada = CrearAsignacion(new DateOnly(2026, 1, 1), hoy);

        cerrada.SeSolapaCon(hoy, null).Should().BeFalse();
    }

    [Fact]
    public void No_hay_solape_entre_dos_periodos_cerrados_consecutivos()
    {
        var primera = CrearAsignacion(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1));

        primera.SeSolapaCon(new DateOnly(2026, 3, 1), new DateOnly(2026, 6, 1)).Should().BeFalse();
    }

    [Fact]
    public void Dos_asignaciones_abiertas_siempre_se_solapan()
    {
        var abierta = CrearAsignacion(new DateOnly(2026, 1, 1));

        abierta.SeSolapaCon(new DateOnly(2026, 6, 1), null).Should().BeTrue();
    }

    [Fact]
    public void No_hay_solape_cuando_los_rangos_estan_separados()
    {
        var enero = CrearAsignacion(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1));

        enero.SeSolapaCon(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1)).Should().BeFalse();
    }

    [Fact]
    public void El_solape_es_simetrico()
    {
        var a = CrearAsignacion(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1));
        var b = CrearAsignacion(new DateOnly(2026, 3, 1));

        a.SeSolapaCon(b.FechaAlta, b.FechaBaja).Should().Be(b.SeSolapaCon(a.FechaAlta, a.FechaBaja));
    }
}
