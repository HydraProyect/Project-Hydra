using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Queries.ObtenerAccesosSoporteTalveg;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Tenants;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// La barrera de Application de la transparencia para el Tenant propietario
/// (ADR-011 § 8.7, incremento 2). La de la base la miden los tests de
/// integración bajo <c>cae_app_runtime</c>.
/// </summary>
public class ObtenerAccesosSoporteTalvegQueryTests
{
    private static readonly Guid TenantPropietario = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();
    private static readonly Guid Administrador = Guid.NewGuid();

    [Fact]
    public async Task Sin_ser_Administrador_del_Tenant_de_origen_no_lee_nada_y_la_seccion_no_existe()
    {
        var handler = new ObtenerAccesosSoporteTalvegQueryHandler(
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: TenantPropietario),
            new AutorizacionDelegacionFalsa(autoriza: false),
            new ContextoQueNoDebeLeerse());

        (await handler.Handle(new ObtenerAccesosSoporteTalvegQuery(), CancellationToken.None)).Should().BeNull(
            "null, no una lista vacía: la pantalla oculta la sección en lugar de enseñar «ningún acceso»");
    }

    [Fact]
    public async Task Pregunta_por_el_Tenant_de_origen_y_no_por_otro()
    {
        var autorizacion = AutorizacionDelegacionFalsa.AdministradorDe(TenantPropietario);
        var handler = new ObtenerAccesosSoporteTalvegQueryHandler(
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: TenantPropietario),
            autorizacion,
            new ContextoConSesiones());

        (await handler.Handle(new ObtenerAccesosSoporteTalvegQuery(), CancellationToken.None)).Should().NotBeNull();
        autorizacion.UltimoTenantConsultado.Should().Be(TenantPropietario);
    }

    [Fact]
    public async Task Sin_usuario_o_sin_Tenant_de_origen_no_lee_nada()
    {
        var sinTenant = new ObtenerAccesosSoporteTalvegQueryHandler(
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: null),
            new AutorizacionDelegacionFalsa(autoriza: true),
            new ContextoQueNoDebeLeerse());
        var sinUsuario = new ObtenerAccesosSoporteTalvegQueryHandler(
            new CurrentUserServiceFalso(usuarioId: null, tenantOrigenId: TenantPropietario),
            new AutorizacionDelegacionFalsa(autoriza: true),
            new ContextoQueNoDebeLeerse());

        (await sinTenant.Handle(new ObtenerAccesosSoporteTalvegQuery(), CancellationToken.None)).Should().BeNull();
        (await sinUsuario.Handle(new ObtenerAccesosSoporteTalvegQuery(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Devuelve_solo_las_del_Tenant_de_origen_con_su_estado_y_la_mas_reciente_primero()
    {
        var contexto = new ContextoConSesiones();
        var handler = new ObtenerAccesosSoporteTalvegQueryHandler(
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: TenantPropietario),
            AutorizacionDelegacionFalsa.AdministradorDe(TenantPropietario),
            contexto);

        var accesos = await handler.Handle(new ObtenerAccesosSoporteTalvegQuery(), CancellationToken.None);

        accesos!.Select(a => (a.Motivo, a.Estado)).Should().Equal(
            ("abierta", EstadoAccesoSoporteTalveg.Abierto),
            ("cerrada", EstadoAccesoSoporteTalveg.Cerrado),
            ("cerrada tras expirar", EstadoAccesoSoporteTalveg.Caducado),
            ("caducada", EstadoAccesoSoporteTalveg.Caducado));
        accesos.Should().NotContain(a => a.Motivo == "de otro Tenant");
        accesos.Single(a => a.Motivo == "abierta").Ticket.Should().Be("TCK-1");
        accesos.Single(a => a.Motivo == "caducada").Capacidad.Should().Be(CapacidadPrivilegio.Aprovisionamiento,
            "el Tenant distingue un aprovisionamiento, que escribe, de una lectura de soporte");
        accesos.Single(a => a.Motivo == "abierta").Capacidad.Should().Be(CapacidadPrivilegio.SoporteLectura);
    }

    private sealed class ContextoConSesiones : IPlataformaQueryContext
    {
        private readonly List<SesionPrivilegiada> _sesiones;

        public ContextoConSesiones()
        {
            var ahora = DateTime.UtcNow;
            var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
                Guid.NewGuid(), ahora.AddDays(-1), ahora.AddDays(1));

            var aprovisionamiento = ConcesionPrivilegio.SobreTenants(
                Guid.NewGuid(), CapacidadPrivilegio.Aprovisionamiento, [TenantPropietario], ahora.AddDays(-1), ahora.AddDays(1));
            var caducada = SesionPrivilegiada.Abrir(aprovisionamiento, TenantPropietario, "caducada", ahora.AddHours(-6), TimeSpan.FromHours(1));
            var cerrada = SesionPrivilegiada.Abrir(concesion, TenantPropietario, "cerrada", ahora.AddHours(-2), TimeSpan.FromHours(4));
            cerrada.Cerrar(ahora.AddHours(-1));
            var cerradaTrasExpirar = SesionPrivilegiada.Abrir(concesion, TenantPropietario, "cerrada tras expirar", ahora.AddHours(-4), TimeSpan.FromHours(1));
            cerradaTrasExpirar.Cerrar(ahora.AddHours(-2.5));
            var abierta = SesionPrivilegiada.Abrir(concesion, TenantPropietario, "abierta", ahora.AddMinutes(-5), TimeSpan.FromHours(1), ticket: "TCK-1");
            var ajena = SesionPrivilegiada.Abrir(concesion, OtroTenant, "de otro Tenant", ahora.AddMinutes(-1), TimeSpan.FromHours(1));

            _sesiones = [caducada, ajena, cerradaTrasExpirar, abierta, cerrada];
        }

        public IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma => Vacio<EstadoBootstrapPlataforma>();
        public IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio => Vacio<ConcesionPrivilegio>();
        public IQueryable<SesionPrivilegiada> SesionesPrivilegiadas => new TestAsyncQueryable<SesionPrivilegiada>(_sesiones.AsQueryable());
        public IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion => Vacio<TenantAlcanzadoPorConcesion>();

        private static IQueryable<T> Vacio<T>() => new TestAsyncQueryable<T>(new List<T>().AsQueryable());
    }

    private sealed class ContextoQueNoDebeLeerse : IPlataformaQueryContext
    {
        private static IQueryable<T> Prohibido<T>() =>
            throw new InvalidOperationException("Lectura del plano de privilegio antes de autorizar.");

        public IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma => Prohibido<EstadoBootstrapPlataforma>();
        public IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio => Prohibido<ConcesionPrivilegio>();
        public IQueryable<SesionPrivilegiada> SesionesPrivilegiadas => Prohibido<SesionPrivilegiada>();
        public IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion => Prohibido<TenantAlcanzadoPorConcesion>();
    }
}
