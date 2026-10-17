using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Las definiciones del Cuadro de dirección evaluadas en memoria con la misma expresión que la consulta
/// traduce a SQL. Fechas en UTC; el mes es el de Madrid (septiembre de 2026, horario de verano: UTC+2).
/// </summary>
public class DefinicionesCuadroDireccionTests
{
    private static readonly MesCuadroDireccion Septiembre = new(2026, 9);

    private static DateTime Utc(int mes, int dia, int hora, int minuto = 0) => new(2026, mes, dia, hora, minuto, 0, DateTimeKind.Utc);

    private static VidaDeRegistro Vida(DateTime creado, DateTime? eliminado = null, bool? estaEliminado = null) =>
        new() { CreadoEnUtc = creado, EstaEliminado = estaEliminado ?? eliminado is not null, EliminadoEnUtc = eliminado };

    private static bool Cumple(VidaDeRegistro vida, System.Linq.Expressions.Expression<Func<VidaDeRegistro, bool>> definicion) =>
        new[] { vida }.AsQueryable().Count(definicion) == 1;

    [Fact]
    public void El_mes_empieza_y_acaba_a_las_00_00_de_Madrid()
    {
        Septiembre.InicioUtc.Should().Be(Utc(8, 31, 22));
        Septiembre.FinUtc.Should().Be(Utc(9, 30, 22));
        new MesCuadroDireccion(2026, 10).FinUtc.Should().Be(Utc(10, 31, 23), "el 25 de octubre Madrid vuelve a UTC+1");
    }

    [Fact]
    public void El_mes_en_curso_es_parcial_con_corte_ahora_y_el_terminado_estimado_con_corte_al_final()
    {
        var ahora = Utc(9, 15, 10);
        Septiembre.EstadoA(ahora).Should().Be(EstadoCifraMensual.Parcial);
        Septiembre.CorteA(ahora).Should().Be(ahora);

        var despues = Utc(10, 2, 10);
        Septiembre.EstadoA(despues).Should().Be(EstadoCifraMensual.Estimado);
        Septiembre.CorteA(despues).Should().Be(Septiembre.FinUtc);

        Septiembre.EstadoA(Septiembre.FinUtc).Should().Be(EstadoCifraMensual.Estimado, "el fin es exclusivo: a las 00:00 del día 1 el mes ya terminó");
    }

    [Fact]
    public void Un_trabajador_dado_de_alta_y_no_eliminado_es_activo_aunque_no_tenga_asignaciones()
    {
        // La definición no mira Asignaciones ni Visitas: ni una vigente, ni una futura, ni ninguna.
        var vida = Vida(creado: Utc(3, 1, 9));

        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Septiembre.FinUtc)).Should().BeTrue();
        Cumple(vida, DefinicionesCuadroDireccion.AltaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeFalse();
    }

    [Fact]
    public void Eliminado_el_ultimo_dia_del_mes_en_hora_de_Madrid_es_baja_de_ese_mes_y_no_activo_a_fin_de_mes()
    {
        // 30 de septiembre a las 23:30 de Madrid = 21:30 UTC: todavía septiembre.
        var vida = Vida(creado: Utc(1, 10, 9), eliminado: Utc(9, 30, 21, 30));

        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeTrue();
        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Septiembre.FinUtc)).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Utc(9, 30, 20))).Should().BeTrue("antes de eliminarse seguía activo");
        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(new MesCuadroDireccion(2026, 10).InicioUtc, new MesCuadroDireccion(2026, 10).FinUtc))
            .Should().BeFalse();
    }

    [Fact]
    public void Eliminado_a_las_00_30_del_dia_1_en_Madrid_es_baja_del_mes_siguiente()
    {
        // 1 de octubre a las 00:30 de Madrid = 30 de septiembre 22:30 UTC.
        var vida = Vida(creado: Utc(1, 10, 9), eliminado: Utc(9, 30, 22, 30));

        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Septiembre.FinUtc)).Should().BeTrue();
    }

    [Fact]
    public void La_baja_automatica_por_RNT_o_ITA_y_la_eliminacion_a_mano_son_la_misma_baja()
    {
        // Las dos son MarcarComoEliminado: la definición no distingue el motivo y no lee quién la hizo.
        var vida = Vida(creado: Utc(1, 10, 9), eliminado: Utc(9, 12, 8));

        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Utc(9, 20, 0))).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeTrue();
    }

    [Fact]
    public void Un_registro_restaurado_no_cuenta_como_baja_y_vuelve_a_estar_activo()
    {
        var vida = Vida(creado: Utc(1, 10, 9), eliminado: null, estaEliminado: false);

        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Septiembre.FinUtc)).Should().BeTrue();
    }

    [Fact]
    public void Un_eliminado_sin_fecha_de_eliminacion_no_cuenta_como_activo_ni_como_baja()
    {
        var vida = Vida(creado: Utc(1, 10, 9), eliminado: null, estaEliminado: true);

        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Septiembre.FinUtc)).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeFalse();
    }

    [Fact]
    public void Dado_de_alta_y_eliminado_en_el_mismo_mes_es_alta_y_baja_y_no_activo_al_cierre()
    {
        var vida = Vida(creado: Utc(9, 3, 9), eliminado: Utc(9, 20, 9));

        Cumple(vida, DefinicionesCuadroDireccion.AltaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeTrue();
        Cumple(vida, DefinicionesCuadroDireccion.BajaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeTrue();
        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(Septiembre.FinUtc)).Should().BeFalse();
    }

    [Fact]
    public void Dado_de_alta_despues_del_corte_no_es_activo_en_el_corte()
    {
        var corte = Utc(9, 15, 10);
        var vida = Vida(creado: Utc(9, 15, 11));

        Cumple(vida, DefinicionesCuadroDireccion.VivoAlCorte(corte)).Should().BeFalse();
        Cumple(vida, DefinicionesCuadroDireccion.AltaEnElMes(Septiembre.InicioUtc, Septiembre.FinUtc)).Should().BeTrue();
    }

    [Fact]
    public void Una_conversacion_con_varios_entrantes_es_una_sola_solicitud_en_la_fecha_del_primero()
    {
        var conversacion = Guid.NewGuid();
        var mensajes = new[]
        {
            new Mensaje(conversacion, DireccionMensaje.Saliente, CanalConversacion.Correo, "gestora@operador.test", "<p>Hola</p>", Utc(9, 1, 8)),
            new Mensaje(conversacion, DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "<p>Segundo</p>", Utc(9, 10, 9)),
            new Mensaje(conversacion, DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "<p>Primero</p>", Utc(9, 2, 9)),
            new Mensaje(conversacion, DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "<p>Tercero</p>", Utc(9, 20, 9)),
        };

        mensajes.AsQueryable().PrimerosEntrantes().Should().Equal(Utc(9, 2, 9));
    }

    [Fact]
    public void Una_conversacion_sin_entrantes_no_es_solicitud_y_cada_conversacion_cuenta_una_vez()
    {
        var soloSalientes = Guid.NewGuid();
        var otra = Guid.NewGuid();
        var mensajes = new[]
        {
            new Mensaje(soloSalientes, DireccionMensaje.Saliente, CanalConversacion.Correo, "gestora@operador.test", "<p>Recordatorio</p>", Utc(9, 4, 8)),
            new Mensaje(otra, DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600000000", "Hola", Utc(9, 5, 7)),
        };

        mensajes.AsQueryable().PrimerosEntrantes().Should().Equal(Utc(9, 5, 7));
    }

    [Fact]
    public void Una_conversacion_que_empezo_el_mes_anterior_no_es_solicitud_de_este_mes_aunque_reciba_mas_entrantes()
    {
        var conversacion = Guid.NewGuid();
        var mensajes = new[]
        {
            new Mensaje(conversacion, DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "<p>Agosto</p>", Utc(8, 28, 9)),
            new Mensaje(conversacion, DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "<p>Septiembre</p>", Utc(9, 3, 9)),
        };

        mensajes.AsQueryable().PrimerosEntrantes()
            .Count(primero => primero >= Septiembre.InicioUtc && primero < Septiembre.FinUtc)
            .Should().Be(0);
    }

    [Fact]
    public void Las_franjas_van_en_hora_de_Madrid_con_su_cambio_de_horario()
    {
        var franjas = DefinicionesCuadroDireccion.PorFranja(
        [
            Utc(10, 5, 7, 30),   // lunes 5 de octubre, 09:30 en Madrid (UTC+2)
            Utc(10, 5, 7, 59),   // lunes, 09:59
            Utc(11, 2, 8, 30),   // lunes 2 de noviembre, 09:30 en Madrid (UTC+1)
            Utc(10, 4, 22, 15),  // domingo 4 a las 22:15 UTC = lunes 5 a las 00:15 en Madrid
            Utc(10, 10, 10),     // sábado 10, 12:00
        ]);

        franjas.Should().Equal(
            new FranjaSolicitudesDto(DayOfWeek.Monday, 0, 1),
            new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 3),
            new FranjaSolicitudesDto(DayOfWeek.Saturday, 12, 1));
    }
}
