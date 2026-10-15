using CaeManager.Domain.Proyectos;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Proyectos;

/// <summary>
/// <see cref="VigenciaDeProyecto"/>: una obra está vigente hasta su cierre real (o hasta hoy si sigue abierta); un
/// evento cuenta como Centro temporal, vigente solo entre su inicio y su fin, y el último día cuenta.
/// </summary>
public class VigenciaDeProyectoTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 15);
    private static readonly DateOnly InicioMes = new(2026, 3, 1);
    private static readonly DateOnly FinMes = new(2026, 3, 31);
    private static readonly DateOnly Inicio = new(2026, 3, 5);
    private static readonly DateOnly Fin = new(2026, 3, 8);

    [Fact]
    public void Una_obra_abierta_esta_vigente_hasta_hoy()
    {
        VigenciaDeProyecto.UltimoDiaVigente(TipoProyecto.Obra, null, null, Hoy).Should().Be(Hoy);
    }

    [Fact]
    public void Una_obra_cerrada_esta_vigente_hasta_su_cierre_aunque_su_fin_previsto_sea_anterior()
    {
        VigenciaDeProyecto.UltimoDiaVigente(TipoProyecto.Obra, Fin, new DateOnly(2026, 3, 25), Hoy).Should().Be(new DateOnly(2026, 3, 25));
    }

    [Fact]
    public void Un_evento_abierto_esta_vigente_hasta_su_fin_no_hasta_hoy()
    {
        VigenciaDeProyecto.UltimoDiaVigente(TipoProyecto.Evento, Fin, null, Hoy).Should().Be(Fin);
    }

    [Fact]
    public void Un_evento_cerrado_despues_de_su_fin_sigue_vigente_solo_hasta_su_fin()
    {
        VigenciaDeProyecto.UltimoDiaVigente(TipoProyecto.Evento, Fin, new DateOnly(2026, 3, 25), Hoy).Should().Be(Fin);
    }

    [Fact]
    public void Un_evento_cerrado_antes_de_su_fin_deja_de_estar_vigente_al_cerrarse()
    {
        VigenciaDeProyecto.UltimoDiaVigente(TipoProyecto.Evento, Fin, new DateOnly(2026, 3, 6), Hoy).Should().Be(new DateOnly(2026, 3, 6));
    }

    [Fact]
    public void Una_fila_de_evento_sin_fin_se_trata_como_obra_abierta_en_vez_de_fallar()
    {
        VigenciaDeProyecto.UltimoDiaVigente(TipoProyecto.Evento, null, null, Hoy).Should().Be(Hoy);
    }

    [Fact]
    public void Los_dias_de_un_evento_son_los_de_su_ventana_con_el_ultimo_dia_incluido()
    {
        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Evento, Inicio, Fin, null, Hoy, InicioMes, FinMes).Should().Be(4);
    }

    [Fact]
    public void Los_dias_de_un_evento_cerrado_tarde_no_se_alargan_hasta_el_cierre()
    {
        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Evento, Inicio, Fin, new DateOnly(2026, 3, 25), Hoy, InicioMes, FinMes)
            .Should().Be(4, "como obra abierta hasta el cierre serían 21");
        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Obra, Inicio, Fin, new DateOnly(2026, 3, 25), Hoy, InicioMes, FinMes)
            .Should().Be(21, "control: la misma fila como obra sí cuenta hasta el cierre");
    }

    [Fact]
    public void Un_evento_no_cuenta_dias_en_un_periodo_posterior_a_su_fin_y_una_obra_sin_cerrar_si()
    {
        var abril = (Inicio: new DateOnly(2026, 4, 1), Fin: new DateOnly(2026, 4, 30));

        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Evento, Inicio, Fin, null, Hoy, abril.Inicio, abril.Fin).Should().Be(0);
        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Obra, Inicio, null, null, Hoy, abril.Inicio, abril.Fin).Should().Be(30);
    }

    [Fact]
    public void Los_dias_se_recortan_al_periodo_en_el_inicio_y_en_el_fin()
    {
        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Evento, Inicio, Fin, null, Hoy, new DateOnly(2026, 3, 7), FinMes).Should().Be(2);
        VigenciaDeProyecto.DiasVigentesEnPeriodo(TipoProyecto.Evento, Inicio, Fin, null, Hoy, InicioMes, new DateOnly(2026, 3, 5)).Should().Be(1);
    }

    [Fact]
    public void El_tecnico_de_un_evento_cuenta_si_su_alta_coincide_con_la_ventana_del_evento_dentro_del_periodo()
    {
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(TipoProyecto.Evento, Inicio, Fin, null, Hoy, Inicio, null, InicioMes, FinMes).Should().BeTrue();
    }

    [Fact]
    public void El_tecnico_de_un_evento_no_cuenta_en_un_periodo_posterior_al_fin_aunque_siga_sin_baja()
    {
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Evento, Inicio, Fin, null, Hoy, Inicio, null, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)).Should().BeFalse();
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Obra, Inicio, null, null, Hoy, Inicio, null, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)).Should().BeTrue(
            "control: el técnico de una obra abierta sí sigue contando");
    }

    [Fact]
    public void El_tecnico_de_un_evento_dado_de_alta_tras_el_fin_no_cuenta()
    {
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Evento, Inicio, Fin, null, Hoy, new DateOnly(2026, 3, 9), null, InicioMes, FinMes).Should().BeFalse();
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Evento, Inicio, Fin, null, Hoy, Fin, null, InicioMes, FinMes).Should().BeTrue("el último día del evento es vigente");
    }

    [Fact]
    public void El_tecnico_de_un_evento_con_baja_anterior_al_inicio_no_cuenta()
    {
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Evento, Inicio, Fin, null, Hoy, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 4), InicioMes, FinMes).Should().BeFalse();
    }

    [Fact]
    public void El_tecnico_de_una_obra_sigue_el_criterio_de_siempre_sin_mirar_las_fechas_del_proyecto()
    {
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Obra, new DateOnly(2026, 6, 1), null, null, Hoy, new DateOnly(2026, 3, 10), null, InicioMes, FinMes).Should().BeTrue();
        VigenciaDeProyecto.TecnicoCuentaEnPeriodo(
            TipoProyecto.Obra, Inicio, null, null, Hoy, new DateOnly(2026, 4, 2), null, InicioMes, FinMes).Should().BeFalse();
    }
}
