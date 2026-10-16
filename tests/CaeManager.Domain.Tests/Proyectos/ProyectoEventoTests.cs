using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Proyectos;

public class ProyectoEventoTests
{
    private static readonly DateOnly Inicio = new(2026, 11, 7);
    private static readonly DateOnly Fin = new(2026, 11, 8);

    private static Proyecto Evento() =>
        Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Partido jornada 12", Inicio, Fin, null, TipoProyecto.Evento);

    [Fact]
    public void Un_proyecto_sin_tipo_es_una_obra_y_puede_no_tener_fin()
    {
        var obra = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Ampliación nave 2", Inicio, null, null);

        obra.Tipo.Should().Be(TipoProyecto.Obra);
        obra.EsEvento.Should().BeFalse();
        obra.EventoTerminadoEn(Inicio.AddYears(5)).Should().BeFalse("una obra nunca es un evento terminado");
    }

    [Fact]
    public void Un_evento_exige_fecha_de_fin_al_crearlo()
    {
        var crear = () => Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Partido", Inicio, null, null, TipoProyecto.Evento);

        crear.Should().Throw<ArgumentException>().WithParameterName("fechaFinPrevista");
    }

    [Fact]
    public void Un_evento_no_puede_quedarse_sin_fecha_de_fin_al_editarlo()
    {
        var evento = Evento();

        var actualizar = () => evento.Actualizar("Partido jornada 12", null, null);

        actualizar.Should().Throw<ArgumentException>();
        evento.FechaFinPrevista.Should().Be(Fin);
    }

    [Fact]
    public void Un_tipo_fuera_del_enum_se_rechaza()
    {
        var crear = () => Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "X", Inicio, Fin, null, (TipoProyecto)7);

        crear.Should().Throw<ArgumentException>().WithParameterName("tipo");
    }

    [Fact]
    public void El_ultimo_dia_el_evento_sigue_vigente_y_al_dia_siguiente_esta_terminado()
    {
        var evento = Evento();

        evento.EventoTerminadoEn(Inicio.AddDays(-30)).Should().BeFalse("un evento futuro no ha terminado");
        evento.EventoTerminadoEn(Fin).Should().BeFalse("el día de fin cuenta como vigente");
        evento.EventoTerminadoEn(Fin.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void Un_evento_cerrado_esta_terminado_aunque_no_haya_llegado_su_fin()
    {
        var evento = Evento();
        evento.Cerrar(Inicio);

        evento.EventoTerminadoEn(Inicio).Should().BeTrue();
    }

    [Fact]
    public void La_plantilla_guarda_cada_tipo_una_vez_y_no_admite_quedar_vacia()
    {
        var tipo = Guid.NewGuid();

        var plantilla = PlantillaRequisitosEvento.Crear(Guid.NewGuid(), "  Partidos de liga  ", [tipo, tipo, Guid.NewGuid()]);

        plantilla.Nombre.Should().Be("Partidos de liga");
        plantilla.Tipos.Should().HaveCount(2);
        plantilla.Tipos.Should().OnlyContain(t => t.PlantillaId == plantilla.Id);
        var vacia = () => PlantillaRequisitosEvento.Crear(Guid.NewGuid(), "Vacía", []);
        vacia.Should().Throw<ArgumentException>();
    }


    // ---------- Cierre automático del evento terminado (decisión 2026-10-16) ----------

    [Fact]
    public void Un_evento_abierto_pasado_su_fin_se_cierra_con_su_fecha_de_fin_y_no_con_la_del_pase()
    {
        var evento = Evento();

        var cerrado = evento.CerrarEventoTerminado(Fin.AddDays(10));

        cerrado.Should().BeTrue();
        evento.EstaAbierto.Should().BeFalse();
        evento.FechaCierreReal.Should().Be(Fin, "el evento terminó su último día, no el día en que corrió el proceso");
    }

    [Fact]
    public void El_ultimo_dia_del_evento_todavia_no_se_cierra()
    {
        var evento = Evento();

        evento.CerrarEventoTerminado(Fin).Should().BeFalse("el último día cuenta como vigente");
        evento.EstaAbierto.Should().BeTrue();
        evento.CerrarEventoTerminado(Fin.AddDays(1)).Should().BeTrue("al día siguiente ya ha terminado");
    }

    [Fact]
    public void Una_obra_pasada_de_su_fin_previsto_no_se_cierra_sola()
    {
        var obra = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Ampliación nave 2", Inicio, Fin, null);

        obra.CerrarEventoTerminado(Fin.AddDays(30)).Should().BeFalse();
        obra.EstaAbierto.Should().BeTrue("las obras no cambian: siguen abiertas hasta que alguien las cierra");
    }

    [Fact]
    public void Un_evento_ya_cerrado_a_mano_conserva_su_fecha_de_cierre()
    {
        var evento = Evento();
        evento.Cerrar(Inicio);

        evento.CerrarEventoTerminado(Fin.AddDays(5)).Should().BeFalse();
        evento.FechaCierreReal.Should().Be(Inicio);
    }

    [Fact]
    public void Un_evento_cerrado_con_la_fecha_de_fin_ya_pasada_no_se_reabre()
    {
        var evento = Evento();
        evento.CerrarEventoTerminado(Fin.AddDays(3));

        var reabrir = () => evento.Reabrir(Fin.AddDays(3));

        reabrir.Should().Throw<InvalidOperationException>("el proceso programado lo volvería a cerrar en su siguiente pase");
        evento.EstaAbierto.Should().BeFalse();
    }

    [Fact]
    public void Un_evento_cerrado_se_reabre_tras_cambiar_su_fecha_de_fin_a_una_futura_y_vuelve_a_contar()
    {
        var hoy = Fin.AddDays(3);
        var evento = Evento();
        evento.CerrarEventoTerminado(hoy);

        evento.Actualizar(evento.Nombre, hoy.AddDays(2), null);
        evento.Reabrir(hoy);

        evento.EstaAbierto.Should().BeTrue();
        evento.EventoTerminadoEn(hoy).Should().BeFalse("con la nueva fecha de fin vuelve a ser un evento vigente");
        evento.CerrarEventoTerminado(hoy).Should().BeFalse("no se vuelve a cerrar hasta pasar la nueva fecha de fin");
    }

    [Fact]
    public void Un_evento_cerrado_se_reabre_con_la_nueva_fecha_de_fin_en_el_mismo_paso()
    {
        var hoy = Fin.AddDays(3);
        var evento = Evento();
        evento.CerrarEventoTerminado(hoy);

        evento.Reabrir(hoy, hoy.AddDays(5));

        evento.EstaAbierto.Should().BeTrue();
        evento.FechaFinPrevista.Should().Be(hoy.AddDays(5));
        var conFechaPasada = () => Evento().Reabrir(hoy, Fin);
        conFechaPasada.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_obra_no_cambia_su_fecha_de_fin_al_reabrirse()
    {
        var obra = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Obra", Inicio, Fin, null);
        obra.Cerrar(Fin);

        var reabrir = () => obra.Reabrir(Fin, Fin.AddDays(9));

        reabrir.Should().Throw<InvalidOperationException>();
        obra.EstaAbierto.Should().BeFalse();
    }

    [Fact]
    public void El_predicado_de_pendientes_de_cierre_es_el_complemento_de_sin_terminar_entre_los_eventos_abiertos()
    {
        var abiertoPasado = Evento();
        var abiertoVigente = Evento();
        var cerradoPasado = Evento();
        cerradoPasado.Cerrar(Inicio);
        var obraPasada = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Obra", Inicio, Fin, null);

        var pendiente = VigenciaEvento.PendienteDeCierreEn(Fin.AddDays(1)).Compile();
        var pendienteEnFin = VigenciaEvento.PendienteDeCierreEn(Fin).Compile();

        pendiente(abiertoPasado).Should().BeTrue();
        pendienteEnFin(abiertoVigente).Should().BeFalse("el último día cuenta como vigente");
        pendiente(cerradoPasado).Should().BeFalse("ya está cerrado");
        pendiente(obraPasada).Should().BeFalse("una obra no se cierra sola");
    }
}
