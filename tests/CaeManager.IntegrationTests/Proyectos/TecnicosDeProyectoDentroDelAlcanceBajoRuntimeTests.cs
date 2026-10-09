using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>
/// <b>A quien se le nombra un técnico de Proyecto, ese Trabajador está dentro de su alcance.</b>
/// Las dos lecturas que nombran técnicos (<see cref="ObtenerProyectosQuery"/>, la fila del listado, y
/// <see cref="ObtenerTecnicosProyectoQuery"/>, el panel y la página del Proyecto) no cruzan con
/// <c>ObtenerTrabajadorIdsVisiblesAsync</c>, y hoy no hace falta: quien ve un Proyecto tiene alcance
/// total dentro del Tenant propietario (Administrador, Dirección CAE, Consulta) o una Asignación de
/// Cartera, que es siempre sobre el Tenant entero (D-7) y alcanza a todos sus Trabajadores, también
/// al de una Subcontrata sin ninguna Asignación a un Centro.
///
/// <para>
/// Contra PostgreSQL real, autenticando como <c>cae_app_runtime</c> —RLS siempre aplica— con el
/// <see cref="TenantRlsConnectionInterceptor"/> real, y con el <see cref="AlcanceDatosService"/> y los
/// handlers de producción.
/// </para>
///
/// <para>
/// <b>Hueco declarado, con su control positivo.</b> El modelo admite una Asignación de Operación
/// acotada a un Cliente empresarial, y bajo ella un Gestor CAE ve el Proyecto de ese Cliente pero no
/// a un técnico de Subcontrata sin Asignación en sus Centros: ahí las dos lecturas sí lo nombrarían.
/// Ningún código de producción crea esa operación (<c>RepartoDeCarteraPorClienteRetiradoTests</c>);
/// el último test la fabrica a mano para demostrar que este instrumento ve la diferencia. Quien
/// añada un productor de operaciones acotadas tiene que cruzar antes las dos lecturas con
/// <c>ObtenerTrabajadorIdsVisiblesAsync</c> y dar la vuelta a ese test.
/// </para>
/// </summary>
public class TecnicosDeProyectoDentroDelAlcanceBajoRuntimeTests : IAsyncLifetime
{
    private static readonly DateOnly Inicio = new(2026, 3, 12);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _propietario = new("Tenant propietario de prueba");
    private readonly Tenant _operadorExterno = new("Operador CAE externo de prueba");
    private readonly Tenant _otroPropietario = new("Otro Tenant propietario de prueba");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _direccion = Guid.NewGuid();
    private readonly Guid _consulta = Guid.NewGuid();
    private readonly Guid _gestorExterno = Guid.NewGuid();
    private readonly Guid _gestorSinCartera = Guid.NewGuid();
    private readonly Guid _gestorBajoOperacionAcotada = Guid.NewGuid();

    private Guid _clienteA;
    private Guid _proyectoA;
    private Guid _tecnicoDeLaEmpresaPropia;
    private Guid _tecnicoDeSubcontrataSinAsignacion;
    private Guid _tecnicoDeSubcontrataAsignadoAOtroCliente;

    private Guid _clienteDelOtroTenant;
    private Guid _proyectoDelOtroTenant;

    public async Task InitializeAsync()
    {
        var ahora = DateTime.UtcNow;

        await using (var contexto = ContextoDeSiembra(_propietario.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_propietario, _operadorExterno, _otroPropietario);

            // Los roles los siembran las migraciones.
            var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

            void Cuenta(Guid id, Guid tenantId, string rol)
            {
                contexto.Users.Add(new ApplicationUser
                {
                    Id = id,
                    TenantId = tenantId,
                    UserName = $"{id:N}@caemanager.local",
                    NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                    Email = $"{id:N}@caemanager.local",
                    NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                    NombreCompleto = rol,
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString(),
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                });
                contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
            }

            Cuenta(_administrador, _propietario.Id, Roles.Administrador);
            Cuenta(_direccion, _propietario.Id, Roles.DireccionCae);
            Cuenta(_consulta, _propietario.Id, Roles.Consulta);
            Cuenta(_gestorSinCartera, _propietario.Id, Roles.GestorCae);
            Cuenta(_gestorBajoOperacionAcotada, _propietario.Id, Roles.GestorCae);
            // La cuenta del Operador CAE externo es GestorCae en SU organización: el rol con el que
            // opera aquí lo da la Asignación de Operador Delegado.
            Cuenta(_gestorExterno, _operadorExterno.Id, Roles.GestorCae);

            var clienteA = Empresa.CrearComoCliente("Cliente empresarial A", "B10380186", false, null, null);
            var clienteB = Empresa.CrearComoCliente("Cliente empresarial B", "B10380202", false, null, null);
            var propia = new Empresa("Empresa propia", "B10380194");
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata", null, "Estandar");
            contexto.Empresas.AddRange(clienteA, clienteB, propia, subcontrata);

            var centroA = new Centro(clienteA.Id, propia.Id, "Centro del Cliente A");
            var centroB = new Centro(clienteB.Id, propia.Id, "Centro del Cliente B");
            contexto.Centros.AddRange(centroA, centroB);

            var dePropia = Trabajador.DeEmpresa(propia.Id, "Nora", "Vidal", "12345678Z");
            var deSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Leo", "Mas", "11111111H");
            var deSubcontrataEnB = Trabajador.DeSubcontrata(subcontrata.Id, "Iker", "Sanz", "87654321X");
            contexto.Trabajadores.AddRange(dePropia, deSubcontrata, deSubcontrataEnB);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(deSubcontrataEnB.Id, centroB.Id, new DateOnly(2026, 1, 1)));

            var proyecto = Proyecto.Crear(clienteA.Id, centroA.Id, "Reforma nave", Inicio, null, null);
            contexto.Proyectos.Add(proyecto);
            contexto.ProyectosTecnicos.AddRange(
                new ProyectoTecnico(proyecto.Id, dePropia.Id, Inicio),
                new ProyectoTecnico(proyecto.Id, deSubcontrata.Id, Inicio),
                new ProyectoTecnico(proyecto.Id, deSubcontrataEnB.Id, Inicio));

            // Lo único que el producto crea: la raíz, la operación externa universal y carteras del
            // Tenant entero.
            var raiz = AsignacionOperacion.Raiz(_propietario.Id, ServicioCae.Outbound, ahora.AddDays(-1), ahora);
            var externa = AsignacionOperacion.Externa(
                _propietario.Id, _operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora);
            contexto.AsignacionesOperacion.AddRange(raiz, externa);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                externa, _gestorExterno, Roles.GestorCae, AmbitoAsignacion.Universal, ahora.AddDays(-1), vigenciaHasta: null, ahora));

            var delegacion = new DelegacionTenant(_operadorExterno.Id, _propietario.Id);
            contexto.DelegacionesTenant.Add(delegacion);
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(delegacion.Id, _gestorExterno, Roles.GestorCae));

            // Lo que el producto NO crea (control positivo del último test): una operación acotada
            // al Cliente empresarial A, con la cartera universal de un Gestor CAE colgando de ella.
            var acotada = AsignacionOperacion.Interna(
                _propietario.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteA.Id), ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(acotada);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                acotada, _gestorBajoOperacionAcotada, AmbitoAsignacion.Universal, ahora.AddDays(-1), vigenciaHasta: null, ahora));

            await contexto.SaveChangesAsync();

            _clienteA = clienteA.Id;
            _proyectoA = proyecto.Id;
            _tecnicoDeLaEmpresaPropia = dePropia.Id;
            _tecnicoDeSubcontrataSinAsignacion = deSubcontrata.Id;
            _tecnicoDeSubcontrataAsignadoAOtroCliente = deSubcontrataEnB.Id;
        }

        await using (var contexto = ContextoDeSiembra(_otroPropietario.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial del otro Tenant", "B10380186", false, null, null);
            var propia = new Empresa("Empresa propia del otro Tenant", "B10380194");
            contexto.Empresas.AddRange(cliente, propia);
            var centro = new Centro(cliente.Id, propia.Id, "Centro del otro Tenant");
            contexto.Centros.Add(centro);
            var trabajador = Trabajador.DeEmpresa(propia.Id, "Ada", "Roca", "12345678Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Proyecto del otro Tenant", Inicio, null, null);
            contexto.Proyectos.Add(proyecto);
            contexto.ProyectosTecnicos.Add(new ProyectoTecnico(proyecto.Id, trabajador.Id, Inicio));
            await contexto.SaveChangesAsync();

            _clienteDelOtroTenant = cliente.Id;
            _proyectoDelOtroTenant = proyecto.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private Guid[] LosTresTecnicos =>
        [_tecnicoDeLaEmpresaPropia, _tecnicoDeSubcontrataSinAsignacion, _tecnicoDeSubcontrataAsignadoAOtroCliente];

    // ── La invariante ─────────────────────────────────────────────────────

    [Fact]
    public async Task El_Gestor_CAE_del_Operador_CAE_externo_con_cartera_tiene_en_su_alcance_a_todos_los_tecnicos_que_se_le_nombran()
    {
        var lectura = await LeerAsync(_gestorExterno, Roles.GestorCae, _operadorExterno.Id);

        lectura.NombradosEnLaLista.Should().BeEquivalentTo(LosTresTecnicos);
        lectura.NombradosEnElPanel.Should().BeEquivalentTo(LosTresTecnicos);

        // Lista explícita, no null: la cartera es autoridad de Operación. Si fuera null, el «dentro
        // del alcance» de abajo no estaría midiendo nada.
        lectura.TrabajadoresVisibles.Should().NotBeNull();
        lectura.NombradosEnLaLista.Except(lectura.TrabajadoresVisibles!).Should().BeEmpty(
            "la cartera del Tenant entero alcanza a todos los Trabajadores, también al de Subcontrata sin Asignación");
        lectura.NombradosEnElPanel.Except(lectura.TrabajadoresVisibles!).Should().BeEmpty();
    }

    public static TheoryData<string> RolesDeAlcanceTotalDelTenantPropietario =>
        new() { Roles.Administrador, Roles.DireccionCae, Roles.Consulta };

    [Theory]
    [MemberData(nameof(RolesDeAlcanceTotalDelTenantPropietario))]
    public async Task Los_roles_de_alcance_total_del_Tenant_propietario_ven_a_los_tecnicos_sin_restriccion(string rol)
    {
        var cuenta = rol switch
        {
            Roles.Administrador => _administrador,
            Roles.DireccionCae => _direccion,
            _ => _consulta,
        };

        var lectura = await LeerAsync(cuenta, rol, _propietario.Id);

        lectura.NombradosEnLaLista.Should().BeEquivalentTo(LosTresTecnicos);
        lectura.NombradosEnElPanel.Should().BeEquivalentTo(LosTresTecnicos);
        lectura.TrabajadoresVisibles.Should().BeNull("sin restricción dentro del Tenant propietario");
    }

    // ── Quien no ve el Proyecto no recibe a sus técnicos ──────────────────

    [Fact]
    public async Task Un_Gestor_CAE_sin_cartera_vigente_no_recibe_tecnicos_ni_por_la_lista_ni_pidiendo_el_Proyecto_por_su_id()
    {
        var lectura = await LeerAsync(_gestorSinCartera, Roles.GestorCae, _propietario.Id);

        lectura.ProyectosEnLaLista.Should().BeEmpty();
        lectura.NombradosEnElPanel.Should().BeEmpty(
            "ObtenerTecnicosProyectoQuery aplica el alcance del Proyecto por sí misma, no depende de que el " +
            "componente haya pedido antes el detalle (control positivo: los tests de arriba reciben tres)");
        lectura.TrabajadoresVisibles.Should().BeEmpty();
    }

    [Fact]
    public async Task Los_tecnicos_de_un_Proyecto_de_otro_Tenant_no_se_nombran_bajo_RLS()
    {
        await using var contexto = ContextoRuntime(_gestorExterno, Roles.GestorCae, _operadorExterno.Id);
        var alcance = Alcance(contexto, _gestorExterno, Roles.GestorCae, _operadorExterno.Id);

        var panel = await new ObtenerTecnicosProyectoQueryHandler(contexto, contexto, alcance)
            .Handle(new ObtenerTecnicosProyectoQuery(_proyectoDelOtroTenant), CancellationToken.None);
        var lista = await new ObtenerProyectosQueryHandler(contexto, contexto, contexto, alcance)
            .Handle(new ObtenerProyectosQuery(_clienteDelOtroTenant), CancellationToken.None);

        panel.Should().BeEmpty();
        lista.Should().BeEmpty();
        // La fila no llega a la conexión de runtime: no es solo que el handler la descarte.
        (await contexto.ProyectosTecnicos.IgnoreQueryFilters().CountAsync(pt => pt.ProyectoId == _proyectoDelOtroTenant))
            .Should().Be(0, "RLS acota la conexión al Tenant propietario en el que se opera");

        // Control positivo: el técnico del otro Tenant existe y su propio Tenant lo ve.
        await using var siembra = ContextoDeSiembra(_otroPropietario.Id);
        (await siembra.ProyectosTecnicos.CountAsync(pt => pt.ProyectoId == _proyectoDelOtroTenant)).Should().Be(1);
    }

    // ── Control positivo: el instrumento ve a un técnico fuera de alcance ─

    [Fact]
    public async Task Hueco_declarado_bajo_una_operacion_acotada_a_un_Cliente_empresarial_si_se_nombraria_a_un_tecnico_fuera_del_alcance()
    {
        var lectura = await LeerAsync(_gestorBajoOperacionAcotada, Roles.GestorCae, _propietario.Id);

        // Ve el Proyecto del Cliente empresarial A, y de sus técnicos solo alcanza al de la Empresa
        // propia: los dos de la Subcontrata no tienen Asignación en ningún Centro de A.
        lectura.ProyectosEnLaLista.Should().Equal(_proyectoA);
        lectura.TrabajadoresVisibles.Should().BeEquivalentTo([_tecnicoDeLaEmpresaPropia]);

        Guid[] fueraDelAlcance = [_tecnicoDeSubcontrataSinAsignacion, _tecnicoDeSubcontrataAsignadoAOtroCliente];
        lectura.NombradosEnLaLista.Except(lectura.TrabajadoresVisibles!).Should().BeEquivalentTo(fueraDelAlcance,
            "es el estado que ningún código de producción crea; si este test cambia de color porque las " +
            "lecturas ya cruzan con ObtenerTrabajadorIdsVisiblesAsync, dale la vuelta: el hueco está cerrado");
        lectura.NombradosEnElPanel.Except(lectura.TrabajadoresVisibles!).Should().BeEquivalentTo(fueraDelAlcance);
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private sealed record Lectura(
        IReadOnlyList<Guid> ProyectosEnLaLista,
        IReadOnlyList<Guid> NombradosEnLaLista,
        IReadOnlyList<Guid> NombradosEnElPanel,
        IReadOnlyList<Guid>? TrabajadoresVisibles);

    /// <summary>Las dos lecturas que nombran técnicos y el alcance de Trabajadores, con la misma cuenta.</summary>
    private async Task<Lectura> LeerAsync(Guid usuarioId, string rol, Guid tenantOrigen)
    {
        await using var contexto = ContextoRuntime(usuarioId, rol, tenantOrigen);
        var alcance = Alcance(contexto, usuarioId, rol, tenantOrigen);

        var lista = await new ObtenerProyectosQueryHandler(contexto, contexto, contexto, alcance)
            .Handle(new ObtenerProyectosQuery(_clienteA), CancellationToken.None);
        var panel = await new ObtenerTecnicosProyectoQueryHandler(contexto, contexto, alcance)
            .Handle(new ObtenerTecnicosProyectoQuery(_proyectoA), CancellationToken.None);

        return new Lectura(
            lista.Select(p => p.Id).ToList(),
            lista.SelectMany(p => p.TecnicosActivos).Select(t => t.TrabajadorId).ToList(),
            panel.Select(t => t.TrabajadorId).ToList(),
            await alcance.ObtenerTrabajadorIdsVisiblesAsync());
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto, Guid usuarioId, string rol, Guid tenantOrigen) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenantOrigen),
            new TenantActualAmbiental { TenantId = _propietario.Id }, new SesionPrivilegiadaAusente());

    /// <summary>Conexión de <c>cae_app_runtime</c> operando en el Tenant propietario: RLS aplica.</summary>
    private CaeManagerDbContext ContextoRuntime(Guid usuarioId, string rol, Guid tenantOrigen)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, new SinTenantSeleccionado(),
                    new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenantOrigen),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private CaeManagerDbContext ContextoDeSiembra(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
