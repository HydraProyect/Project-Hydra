using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Application.Tests.Tenants;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.ApiKeys;

/// <summary>
/// La autoridad de revocar es la concesión AdminPlataforma sobre el Tenant propietario de la
/// clave. El handler ya no recibe ningún acceso a <c>Tenants</c>: la marca de Tenant de
/// plataforma no puede decidir aquí (lo congela también UsosDeEsPlataformaCongeladosTests).
/// </summary>
public class RevocarClaveApiCommandTests
{
    [Theory]
    [InlineData("sin concesion", "ClaveApi.NoAutorizado")]
    [InlineData("concesion sobre otro Tenant", "ClaveApi.NoAutorizado")]
    [InlineData("sin usuario", "ClaveApi.SinUsuario")]
    [InlineData("otro origen", "DelegacionTenant.NoEncontrada")]
    [InlineData("sin origen", "DelegacionTenant.NoEncontrada")]
    [InlineData("proposito comercial", "DelegacionTenant.NoEncontrada")]
    public async Task Rechaza_la_revocacion_sin_autoridad_sobre_el_Tenant_propietario(string caso, string codigo)
    {
        var origen = Guid.NewGuid();
        var propietario = Guid.NewGuid();
        var delegacion = caso == "proposito comercial"
            ? new DelegacionTenant(origen, propietario) : DelegacionTenant.ParaSoporte(origen, propietario);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        var clave = ClaveDePrueba();
        var guardado = new UnitOfWorkFalso();
        var autorizacion = caso == "sin concesion" ? AutorizacionAdminPlataformaFalsa.SinNada()
            : AutorizacionAdminPlataformaFalsa.AcotadaA(caso == "concesion sobre otro Tenant" ? Guid.NewGuid() : propietario);
        var usuario = new CurrentUserServiceFalso(
            caso == "sin usuario" ? null : Guid.NewGuid(),
            tenantOrigenId: caso switch { "otro origen" => Guid.NewGuid(), "sin origen" => null, _ => origen });

        var resultado = await new RevocarClaveApiCommandHandler(delegaciones, new RepositorioClaves(clave), autorizacion, usuario, guardado)
            .Handle(new RevocarClaveApiCommand(delegacion.Id, clave.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
        clave.EstaActiva.Should().BeTrue();
        guardado.VecesGuardado.Should().Be(0);
    }

    // Revocar corta exposición: ni la ventana de Soporte cerrada ni la falta de segundo factor
    // pueden dejar viva una clave filtrada.
    [Theory]
    [InlineData("ventana abierta")]
    [InlineData("ventana cerrada")]
    [InlineData("ventana vencida")]
    [InlineData("sin segundo factor")]
    public async Task Revoca_con_concesion_sobre_el_Tenant_propietario_y_en_su_ambito(string caso)
    {
        var origen = Guid.NewGuid();
        var propietario = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var delegacion = DelegacionTenant.ParaSoporte(origen, propietario);
        if (caso == "ventana abierta") delegacion.ActivarParaSoporte("Prueba", ahora.AddHours(1), ahora);
        if (caso == "ventana vencida") delegacion.ActivarParaSoporte("Prueba", ahora.AddHours(-1), ahora.AddHours(-2));
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        var clave = ClaveDePrueba();
        var claves = new RepositorioClaves(clave);
        var guardado = new UnitOfWorkFalso();
        var autorizacion = AutorizacionAdminPlataformaFalsa.AcotadaA(propietario);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: origen,
            tieneDobleFactorActivo: caso != "sin segundo factor");

        var resultado = await new RevocarClaveApiCommandHandler(delegaciones, claves, autorizacion, usuario, guardado)
            .Handle(new RevocarClaveApiCommand(delegacion.Id, clave.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        clave.EstaActiva.Should().BeFalse();
        guardado.VecesGuardado.Should().Be(1);
        claves.TenantAlLeer.Should().Be(propietario);
        autorizacion.UltimoTenantConsultado.Should().Be(propietario);
        autorizacion.SeConsultoLoGlobal.Should().BeFalse();
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
    }

    private static ClaveApi ClaveDePrueba() =>
        new("Integración Alfa", "hydra_prueba", "hash", Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Prueba");

    private sealed class RepositorioClaves(ClaveApi clave) : IClaveApiRepository
    {
        public Guid? TenantAlLeer { get; private set; }
        public Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            TenantAlLeer = AmbitoTenantExplicito.TenantIdActual;
            return Task.FromResult<ClaveApi?>(id == clave.Id ? clave : null);
        }
        public void Agregar(ClaveApi clave) => throw new NotSupportedException();
        public Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ExisteSoporteDelOrigenEmisorAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CuentaEmisoraRespaldaClaveAsync(Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
