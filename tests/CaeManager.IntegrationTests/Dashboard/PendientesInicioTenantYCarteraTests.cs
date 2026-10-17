using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// Pendientes de Inicio (#1389): PostgreSQL con identidad runtime y RLS real, y Asignación de
/// Cartera real. La cartera universal del Gestor CAE está bajo una Asignación de Operación
/// Outbound acotada a un Cliente empresarial: ese ámbito efectivo excluye el otro Centro del
/// mismo Tenant propietario. Otro Tenant propietario tiene sus propios pendientes como testigo.
/// </summary>
public class PendientesInicioTenantYCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private CaeManagerDbContext _siembra = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _centroDentro;
    private Guid _centroFuera;
    private Guid _proveedorCompartido;
    private Guid _proveedorSoloFuera;

    public async Task InitializeAsync()
    {
        var tenant = new TenantPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, pg => pg.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant)).Options;
        _siembra = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
        await _siembra.Database.MigrateAsync();
        var a = new Tenant("Organización A de prueba");
        var b = new Tenant("Operador CAE externo B de prueba");
        _siembra.Tenants.AddRange(a, b);
        var compartido = new ProveedorPlataformaCae($"test-{Guid.NewGuid():N}", "Plataforma compartida de prueba");
        var soloFuera = new ProveedorPlataformaCae($"test-{Guid.NewGuid():N}", "Plataforma fuera de cartera");
        _siembra.ProveedoresPlataformaCae.AddRange(compartido, soloFuera);
        await _siembra.SaveChangesAsync();
        (_tenantA, _tenantB) = (a.Id, b.Id);
        (_proveedorCompartido, _proveedorSoloFuera) = (compartido.Id, soloFuera.Id);

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var dentro = await SembrarCentroAsync("Dentro", "B10380186", _proveedorCompartido, pendientes: 1, rechazadas: 1);
            var fuera = await SembrarCentroAsync("Fuera", "B10380194", _proveedorCompartido, pendientes: 2, rechazadas: 2);
            await AnadirPendientesAsync(fuera.Centro, fuera.Empresa, fuera.Tipo, _proveedorSoloFuera, pendientes: 1, rechazadas: 0);
            (_centroDentro, _centroFuera) = (dentro.Centro, fuera.Centro);

            var ahora = DateTime.UtcNow;
            var operacion = AsignacionOperacion.Externa(_tenantA, _tenantB, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(dentro.Empresa), ahora.AddDays(-1), null, ahora);
            _siembra.AsignacionesOperacion.Add(operacion);
            _siembra.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _gestor, Roles.GestorCae, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
            await _siembra.SaveChangesAsync();
        }
        using (AmbitoTenantExplicito.Establecer(_tenantB))
            await SembrarCentroAsync("Otro Tenant propietario", "B10380202", _proveedorCompartido, pendientes: 4, rechazadas: 4);
    }

    public async Task DisposeAsync()
    {
        await _siembra.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Inicio_solo_cuenta_pendientes_del_Tenant_propietario_y_centros_de_la_cartera_del_Gestor_CAE()
    {
        // Control de datos: acceso total ve ambos Centros de A, pero nunca los de B.
        var todoA = await ConsultarAsync(_tenantA, Roles.Administrador);
        todoA.Should().HaveCount(2);
        var compartidoA = todoA.Single(p => p.ProveedorPlataformaCaeId == _proveedorCompartido);
        (compartidoA.PendientesDeSubir, compartidoA.Rechazadas).Should().Be((3, 3));
        var soloFuera = todoA.Single(p => p.ProveedorPlataformaCaeId == _proveedorSoloFuera);
        (soloFuera.PendientesDeSubir, soloFuera.Rechazadas).Should().Be((1, 0));

        var todoB = await ConsultarAsync(_tenantB, Roles.Administrador);
        var compartidoB = todoB.Should().ContainSingle().Subject;
        compartidoB.ProveedorPlataformaCaeId.Should().Be(_proveedorCompartido);
        (compartidoB.PendientesDeSubir, compartidoB.Rechazadas).Should().Be((4, 4));

        var cartera = await ConsultarAsync(_tenantA, Roles.GestorCae);
        var visible = cartera.Should().ContainSingle().Subject;
        visible.ProveedorPlataformaCaeId.Should().Be(_proveedorCompartido);
        (visible.PendientesDeSubir, visible.Rechazadas).Should().Be((1, 1),
            "los pendientes del otro Centro de A y los de B no pertenecen al ámbito efectivo del Gestor CAE");
    }

    private async Task<IReadOnlyList<PendientePorPlataformaDto>> ConsultarAsync(Guid tenantId, string rol)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var usuario = new CurrentUserServiceFalso(_gestor, rol, tenantOrigenId: _tenantB);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinSeleccion(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        await using var runtime = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
        var alcance = new AlcanceDatosService(runtime, usuario, tenant, new SinSesionPrivilegiada());
        if (rol == Roles.GestorCae)
        {
            var centros = await alcance.ObtenerCentroIdsVisiblesAsync();
            centros.Should().NotBeNull().And.Contain(_centroDentro).And.NotContain(_centroFuera);
        }
        return await new ObtenerPendientePorPlataformaQueryHandler(runtime, runtime, runtime, alcance)
            .Handle(new ObtenerPendientePorPlataformaQuery(), CancellationToken.None);
    }

    private async Task<(Guid Centro, Guid Empresa, Guid Tipo)> SembrarCentroAsync(
        string nombre, string cif, Guid proveedor, int pendientes, int rechazadas)
    {
        var empresa = Empresa.CrearComoCliente(nombre, cif, false, null, null);
        var tipo = new TipoDocumento($"Seguro {nombre}", null, false, 1, AmbitoAplicacion.Cliente);
        _siembra.Empresas.Add(empresa);
        _siembra.TiposDocumento.Add(tipo);
        await _siembra.SaveChangesAsync();
        var centro = new Centro(empresa.Id, empresa.Id, nombre);
        _siembra.Centros.Add(centro);
        await _siembra.SaveChangesAsync();
        await AnadirPendientesAsync(centro.Id, empresa.Id, tipo.Id, proveedor, pendientes, rechazadas);
        return (centro.Id, empresa.Id, tipo.Id);
    }

    private async Task AnadirPendientesAsync(Guid centro, Guid empresa, Guid tipo, Guid proveedor, int pendientes, int rechazadas)
    {
        var canal = CanalGestionDocumental.DePlataforma(centro, "Acceso", proveedor, null, null, null);
        _siembra.CanalesGestionDocumental.Add(canal);
        for (var i = 0; i < pendientes + rechazadas; i++)
        {
            var documento = Documento.DeCliente(empresa, tipo, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
            _siembra.Documentos.Add(documento);
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            if (i >= pendientes)
                acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Rechazo de prueba", DateTime.UtcNow);
            _siembra.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        }
        await _siembra.SaveChangesAsync();
    }

    private sealed class TenantPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
