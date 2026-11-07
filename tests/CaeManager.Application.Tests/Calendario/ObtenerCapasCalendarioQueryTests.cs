using CaeManager.Application.Calendario;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.CalendarioLaboral;
using FluentAssertions;

namespace CaeManager.Application.Tests.Calendario;

/// <summary>
/// Quién ve los festivos de quién en las capas de /calendario (Especificación de Ausencias y Coberturas, § 2):
/// el Gestor CAE, los suyos; el Coordinador CAE, la Dirección CAE y el Administrador, los de su equipo. El equipo
/// es el del Operador CAE de origen de la sesión, nunca el de otro Operador CAE.
/// </summary>
public class ObtenerCapasCalendarioQueryTests
{
    private static readonly Guid OperadorA = Guid.NewGuid();
    private static readonly Guid OperadorB = Guid.NewGuid();

    private static readonly Guid GestoraA = Guid.NewGuid();
    private static readonly Guid GestorA = Guid.NewGuid();
    private static readonly Guid CoordinadoraA = Guid.NewGuid();
    private static readonly Guid DireccionA = Guid.NewGuid();
    private static readonly Guid GestorB = Guid.NewGuid();
    private static readonly Guid CoordinadorB = Guid.NewGuid();

    /// <summary>
    /// Doble del contexto de lectura que, como el filtro de Tenant de EF y la RLS, solo devuelve los calendarios
    /// del Tenant del ámbito abierto (<see cref="AmbitoTenantExplicito"/>); sin ámbito, ninguno.
    /// </summary>
    private sealed class ContextoPorTenant : ICalendarioLaboralQueryContext
    {
        public List<(Guid Tenant, CalendarioLaboralPersona Calendario)> Calendarios { get; } = [];
        public List<Guid?> TenantsLeidos { get; } = [];

        public IQueryable<FestivoOficial> FestivosOficiales =>
            new TestAsyncQueryable<FestivoOficial>(CatalogoFestivosOficiales.Todos.AsQueryable());

        public IQueryable<CalendarioLaboralPersona> CalendariosLaboralesPersona
        {
            get
            {
                var ambito = AmbitoTenantExplicito.TenantIdActual;
                TenantsLeidos.Add(ambito);
                return new TestAsyncQueryable<CalendarioLaboralPersona>(
                    Calendarios.Where(c => c.Tenant == ambito).Select(c => c.Calendario).ToList().AsQueryable());
            }
        }

        public void Guardar(Guid tenant, Guid usuario, ComunidadAutonoma comunidad, bool intensiva = true) =>
            Calendarios.Add((tenant, CalendarioLaboralPersona.Crear(usuario, new PerfilLaboral(comunidad, null, intensiva))));
    }

    private static (DirectorioRolesEnOrigen Directorio, ContextoPorTenant Contexto) Escenario()
    {
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(GestoraA, OperadorA, "GestorCae");
        directorio.Asignar(GestorA, OperadorA, "GestorCae");
        directorio.Asignar(CoordinadoraA, OperadorA, "CoordinadorCae");
        directorio.Asignar(DireccionA, OperadorA, "DireccionCae");
        directorio.Asignar(GestorB, OperadorB, "GestorCae");
        directorio.Asignar(CoordinadorB, OperadorB, "CoordinadorCae");

        var contexto = new ContextoPorTenant();
        contexto.Guardar(OperadorA, GestoraA, ComunidadAutonoma.Aragon);
        contexto.Guardar(OperadorA, GestorA, ComunidadAutonoma.PaisVasco);
        contexto.Guardar(OperadorA, CoordinadoraA, ComunidadAutonoma.Cataluna, intensiva: false);
        contexto.Guardar(OperadorB, GestorB, ComunidadAutonoma.Extremadura);
        return (directorio, contexto);
    }

    private static Task<CapasCalendarioDto> ConsultarAsync(
        Guid usuario, Guid tenantOrigen, string? rol, DirectorioRolesEnOrigen directorio, ContextoPorTenant contexto,
        int anio = 2026, int mes = 11, string? rolFueraDelOrigen = null) =>
        new ObtenerCapasCalendarioQueryHandler(
                new CurrentUserServicePorAmbito(usuario, tenantOrigen, rol, rolFueraDelOrigen), directorio, contexto)
            .Handle(new ObtenerCapasCalendarioQuery(anio, mes), CancellationToken.None);

    [Fact]
    public async Task El_Gestor_CAE_ve_solo_sus_festivos()
    {
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(GestoraA, OperadorA, "GestorCae", directorio, contexto);

        capas.VeAlEquipo.Should().BeFalse();
        capas.Personas.Should().ContainSingle();
        capas.Comunidades.Should().Equal("Aragón");
        capas.Personas[0].Festivos.Should().Contain(f => f.Fecha == new DateOnly(2026, 11, 2) && f.Lugar == "Aragón");
    }

    [Fact]
    public async Task La_Coordinadora_CAE_ve_las_comunidades_de_su_equipo_y_ninguna_de_otro_Operador_CAE()
    {
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(CoordinadoraA, OperadorA, "CoordinadorCae", directorio, contexto);

        capas.VeAlEquipo.Should().BeTrue();
        capas.Personas.Should().HaveCount(3, "dos Gestores CAE y la Coordinadora CAE del Operador CAE A");
        capas.Comunidades.Should().BeEquivalentTo(["Aragón", "Cataluña", "País Vasco"]);
        capas.Comunidades.Should().NotContain("Extremadura", "es la comunidad del Gestor CAE del Operador CAE B");
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_otro_Operador_CAE_no_ve_las_comunidades_del_equipo_ajeno()
    {
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(GestorB, OperadorB, "GestorCae", directorio, contexto);

        capas.Comunidades.Should().Equal("Extremadura");
        capas.Personas.SelectMany(p => p.Festivos).Should().NotContain(f => f.Lugar == "Aragón" || f.Lugar == "Cataluña" || f.Lugar == "País Vasco");
        contexto.TenantsLeidos.Should().OnlyContain(t => t == OperadorB, "los calendarios se leen en el ámbito del Tenant de origen");
    }

    [Fact]
    public async Task La_Direccion_CAE_ve_al_equipo_pero_no_cuenta_como_persona_del_equipo()
    {
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(DireccionA, OperadorA, "DireccionCae", directorio, contexto);

        capas.VeAlEquipo.Should().BeTrue();
        capas.Personas.Should().HaveCount(3);
    }

    [Fact]
    public async Task Dentro_de_un_Workspace_operativo_derivado_se_lee_el_equipo_del_Tenant_de_origen()
    {
        var (directorio, contexto) = Escenario();

        // El claim de rol fuera del origen es el de la cartera del Workspace operativo derivado; no cambia nada.
        var capas = await ConsultarAsync(CoordinadoraA, OperadorA, "CoordinadorCae", directorio, contexto, rolFueraDelOrigen: "GestorCae");

        capas.VeAlEquipo.Should().BeTrue();
        contexto.TenantsLeidos.Should().OnlyContain(t => t == OperadorA);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_del_equipo_no_aparece()
    {
        var (directorio, contexto) = Escenario();
        directorio.Desactivar(GestorA);

        var capas = await ConsultarAsync(CoordinadoraA, OperadorA, "CoordinadorCae", directorio, contexto);

        capas.Comunidades.Should().NotContain("País Vasco");
    }

    [Fact]
    public async Task Quien_no_tiene_calendario_guardado_ve_solo_los_nacionales_y_la_intensiva_del_equipo()
    {
        var (directorio, contexto) = Escenario();
        var nuevo = Guid.NewGuid();
        directorio.Asignar(nuevo, OperadorA, "GestorCae");

        var diciembre = await ConsultarAsync(nuevo, OperadorA, "GestorCae", directorio, contexto, mes: 12);
        var julio = await ConsultarAsync(nuevo, OperadorA, "GestorCae", directorio, contexto, anio: 2027, mes: 7);

        diciembre.Personas.Should().ContainSingle().Which.Comunidad.Should().BeNull();
        diciembre.Comunidades.Should().BeEmpty();
        // Diciembre de 2026: los nacionales del 8 y el 25, y no los autonómicos del 7 (Aragón, Extremadura) ni del 26 (Cataluña).
        diciembre.Personas[0].Festivos.Select(f => f.Fecha.Day).Should().Equal(8, 25);
        diciembre.Personas[0].Festivos.Should().OnlyContain(f => f.Ambito == AmbitoFestivo.Nacional && f.Lugar == null);
        julio.Personas[0].DiasJornadaIntensiva.Should().HaveCount(22, "los días laborables de julio de 2027");
    }

    [Fact]
    public async Task Sin_el_interruptor_aplica_no_hay_dias_de_intensiva()
    {
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(CoordinadoraA, OperadorA, "CoordinadorCae", directorio, contexto, anio: 2027, mes: 7);

        capas.Personas.Should().Contain(p => p.Comunidad == "Cataluña" && p.DiasJornadaIntensiva.Count == 0);
        capas.Personas.Should().Contain(p => p.Comunidad == "Aragón" && p.DiasJornadaIntensiva.Count > 0);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Otro_rol_no_ve_ninguna_capa(string rol)
    {
        var (directorio, contexto) = Escenario();
        var usuario = Guid.NewGuid();
        directorio.Asignar(usuario, OperadorA, rol);

        var capas = await ConsultarAsync(usuario, OperadorA, rol, directorio, contexto);

        capas.Personas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_ve_nada()
    {
        // Sesión Privilegiada: Soporte TALVEG no es Gestor CAE ni Operador CAE aunque su cuenta tenga un rol en Identity.
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(GestoraA, OperadorA, rol: null, directorio, contexto);

        capas.Personas.Should().BeEmpty();
        contexto.TenantsLeidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_usuario_o_sin_Tenant_de_origen_no_ve_nada()
    {
        var (directorio, contexto) = Escenario();

        var sinUsuario = await new ObtenerCapasCalendarioQueryHandler(
                new CurrentUserServicePorAmbito(null, OperadorA, "GestorCae"), directorio, contexto)
            .Handle(new ObtenerCapasCalendarioQuery(2026, 11), CancellationToken.None);
        var sinOrigen = await new ObtenerCapasCalendarioQueryHandler(
                new CurrentUserServicePorAmbito(GestoraA, null, "GestorCae"), directorio, contexto)
            .Handle(new ObtenerCapasCalendarioQuery(2026, 11), CancellationToken.None);

        sinUsuario.Personas.Should().BeEmpty();
        sinOrigen.Personas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(0, 5)]
    public async Task Un_mes_imposible_no_rompe_la_consulta(int anio, int mes)
    {
        var (directorio, contexto) = Escenario();

        var capas = await ConsultarAsync(GestoraA, OperadorA, "GestorCae", directorio, contexto, anio, mes);

        capas.Personas.Should().BeEmpty();
    }
}
