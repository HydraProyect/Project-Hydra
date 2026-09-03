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
    public void Un_rango_vacio_por_cierre_de_ambito_no_solapa_con_un_alta_posterior()
    {
        // CerrarPorAmbitoEliminado ancla la baja al alta cuando el alta era
        // futura: la fila queda vacía [d, d), sin haber ocupado ni un día.
        // Sin el guard de rango vacío, la fórmula de rangos semiabiertos la
        // trataría como si ocupara [d, ∞) (hallazgo de Codex, REC-064) y
        // bloquearía una alta real y legítima más adelante.
        var vacia = new Asignacion(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 12, 1));
        vacia.CerrarPorAmbitoEliminado(new DateOnly(2026, 8, 30));
        vacia.FechaAlta.Should().Be(vacia.FechaBaja, "el cierre ancló la baja al alta");

        vacia.SeSolapaCon(new DateOnly(2026, 12, 1), null).Should().BeFalse();
        vacia.SeSolapaCon(new DateOnly(2027, 1, 1), null).Should().BeFalse();
    }

    [Fact]
    public void Un_rango_vacio_por_baja_el_mismo_dia_del_alta_no_solapa_con_nada()
    {
        // DarDeBaja permite fechaBaja == FechaAlta (no lo rechaza, solo baja
        // < alta): contratar y cesar a alguien el mismo día también deja un
        // rango vacío, sin pasar por CerrarPorAmbitoEliminado.
        var vacia = CrearAsignacion(new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 10));

        vacia.SeSolapaCon(new DateOnly(2026, 5, 10), null).Should().BeFalse();
        vacia.SeSolapaCon(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)).Should().BeFalse();
    }

    [Fact]
    public void El_solape_es_simetrico()
    {
        var a = CrearAsignacion(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1));
        var b = CrearAsignacion(new DateOnly(2026, 3, 1));

        a.SeSolapaCon(b.FechaAlta, b.FechaBaja).Should().Be(b.SeSolapaCon(a.FechaAlta, a.FechaBaja));
    }
}
