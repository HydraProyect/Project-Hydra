using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Queries.ObtenerEmpresasAsignablesEnAlta;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// La lista de /usuarios de un Coordinador CAE (2026-09-29): solo su equipo, filtrada en Application.
/// Que el puerto filtre de verdad por <c>CoordinadorUsuarioId</c> lo prueba la integración bajo RLS
/// (<c>AsignarCarteraGestorCaeBajoRuntimeTests</c>).
/// </summary>
public class ObtenerEquipoDeCoordinadorQueryTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Coordinador = Guid.NewGuid();
    private static readonly Guid Miembro = Guid.NewGuid();

    private sealed class EquipoFalso : IDirectorioEquipoCoordinador
    {
        public List<Guid> Preguntados { get; } = [];

        public Task<IReadOnlyList<MiembroDeEquipo>> ObtenerEquipoAsync(Guid coordinadorUsuarioId, CancellationToken cancellationToken = default)
        {
            Preguntados.Add(coordinadorUsuarioId);
            return Task.FromResult<IReadOnlyList<MiembroDeEquipo>>([new(Miembro, "g@x.test", "Gestora", true, false)]);
        }
    }

    private static (ObtenerEquipoDeCoordinadorQueryHandler Handler, EquipoFalso Equipo) Handler(
        string? rolEnIdentity, string? rolDeSesion = "CoordinadorCae", bool desactivada = false)
    {
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(Coordinador, Operador, rolEnIdentity);
        if (desactivada) directorio.Desactivar(Coordinador);
        var equipo = new EquipoFalso();
        return (new(new CurrentUserServicePorAmbito(Coordinador, Operador, rolDeSesion), directorio, equipo), equipo);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_activo_recibe_el_equipo_que_le_pertenece_preguntando_por_su_propio_id()
    {
        var (handler, equipo) = Handler("CoordinadorCae");

        (await handler.Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().ContainSingle().Which.Id.Should().Be(Miembro);
        equipo.Preguntados.Should().Equal(Coordinador);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Quien_no_es_Coordinador_CAE_no_recibe_ninguna_lista_de_equipo(string? rol)
    {
        var (handler, equipo) = Handler(rol);

        (await handler.Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().BeEmpty();
        equipo.Preguntados.Should().BeEmpty("ni siquiera se consulta el directorio");
    }

    [Fact]
    public async Task Una_cuenta_desactivada_o_una_sesion_sin_rol_de_negocio_no_recibe_nada()
    {
        var (desactivada, e1) = Handler("CoordinadorCae", desactivada: true);
        (await desactivada.Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().BeEmpty();

        var (sinRol, e2) = Handler("CoordinadorCae", rolDeSesion: null);
        (await sinRol.Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().BeEmpty();

        e1.Preguntados.Should().BeEmpty();
        e2.Preguntados.Should().BeEmpty();
    }

    /// <summary>
    /// Decide el rol efectivo de la sesión en el Tenant de origen (decisión P7) e Identity lo
    /// confirma: una cuenta de Coordinador CAE con la sesión limitada a Consulta, o abierta antes
    /// del cambio de rol, no recibe el equipo; y la sesión de Coordinador CAE de una cuenta que ya
    /// no lo es, tampoco.
    /// </summary>
    [Theory]
    [InlineData("Consulta", "CoordinadorCae")]
    [InlineData("GestorCae", "CoordinadorCae")]
    [InlineData("CoordinadorCae", "GestorCae")]
    public async Task Sin_coincidir_el_rol_efectivo_de_la_sesion_y_el_de_Identity_no_recibe_el_equipo(string rolDeSesion, string rolEnIdentity)
    {
        var (handler, equipo) = Handler(rolEnIdentity, rolDeSesion);

        (await handler.Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().BeEmpty();
        equipo.Preguntados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_rol_de_Coordinador_CAE_en_otro_Tenant_no_da_el_equipo()
    {
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(Coordinador, Guid.NewGuid(), "CoordinadorCae");
        var equipo = new EquipoFalso();

        (await new ObtenerEquipoDeCoordinadorQueryHandler(
                new CurrentUserServicePorAmbito(Coordinador, Operador, "CoordinadorCae"), directorio, equipo)
            .Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().BeEmpty();
        equipo.Preguntados.Should().BeEmpty();
    }

    [Fact]
    public async Task Soporte_TALVEG_sin_Sesion_Privilegiada_no_recibe_un_equipo_de_Coordinador_CAE()
    {
        // Cuenta de Soporte TALVEG con sesión normal y rol Administrador en el Tenant de TALVEG:
        // no es Coordinador CAE de ningún Operador CAE.
        var tenantTalveg = Guid.NewGuid();
        var soporte = Guid.NewGuid();
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(soporte, tenantTalveg, "Administrador");
        var equipo = new EquipoFalso();

        (await new ObtenerEquipoDeCoordinadorQueryHandler(
                new CurrentUserServicePorAmbito(soporte, tenantTalveg, "Administrador"), directorio, equipo)
            .Handle(new ObtenerEquipoDeCoordinadorQuery(), default)).Should().BeEmpty();
        equipo.Preguntados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Coordinador_CAE_no_recibe_la_lista_de_empresas_asignables_del_alta()
    {
        var catalogo = new CatalogoIncorporacionCarteraFalso();
        var actor = new CurrentUserServicePorAmbito(Coordinador, Operador, "CoordinadorCae");

        (await new ObtenerEmpresasAsignablesEnAltaQueryHandler(actor, new TenantFijo(Operador), catalogo)
            .Handle(new ObtenerEmpresasAsignablesEnAltaQuery(), default)).Should().BeEmpty();
    }

    private sealed class TenantFijo(Guid id) : CaeManager.Application.Common.ITenantActual
    {
        public Guid? TenantId => id;
    }
}
