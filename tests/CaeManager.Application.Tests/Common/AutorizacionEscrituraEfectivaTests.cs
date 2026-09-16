using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class AutorizacionEscrituraEfectivaTests
{
    private static SesionPrivilegiadaActiva SesionCon(CapacidadPrivilegio capacidad, Guid tenantObjetivoId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivoId, capacidad, null);

    [Fact]
    public async Task Administrador_es_acto_de_administrador_sin_necesitar_sesion_privilegiada()
    {
        var servicio = new AutorizacionEscrituraEfectiva(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(null), new TenantActualFalso(null));

        (await servicio.EsActoDeAdministradorAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData(null)]
    public async Task Otro_rol_sin_sesion_privilegiada_no_es_acto_de_administrador(string? rol)
    {
        var servicio = new AutorizacionEscrituraEfectiva(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol),
            new SesionPrivilegiadaActualFalsa(null), new TenantActualFalso(null));

        (await servicio.EsActoDeAdministradorAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Sesion_de_Aprovisionamiento_sobre_el_tenant_actual_es_acto_de_administrador()
    {
        var tenant = Guid.NewGuid();
        var servicio = new AutorizacionEscrituraEfectiva(
            new CurrentUserServiceFalso(Guid.NewGuid(), null),
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenant)),
            new TenantActualFalso(tenant));

        (await servicio.EsActoDeAdministradorAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Sesion_de_Aprovisionamiento_sobre_otro_tenant_no_es_acto_de_administrador()
    {
        var servicio = new AutorizacionEscrituraEfectiva(
            new CurrentUserServiceFalso(Guid.NewGuid(), null),
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.Aprovisionamiento, Guid.NewGuid())),
            new TenantActualFalso(Guid.NewGuid()));

        (await servicio.EsActoDeAdministradorAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Sesion_de_SoporteLectura_nunca_es_acto_de_administrador()
    {
        var tenant = Guid.NewGuid();
        var servicio = new AutorizacionEscrituraEfectiva(
            new CurrentUserServiceFalso(Guid.NewGuid(), null),
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.SoporteLectura, tenant)),
            new TenantActualFalso(tenant));

        (await servicio.EsActoDeAdministradorAsync()).Should().BeFalse(
            "SoporteLectura es de solo lectura sin excepción implícita, aunque el tenant coincida");
    }

    /// <summary>
    /// Hallazgo de revisión (Codex, previo a este PR): este servicio se invoca
    /// SIEMPRE desde dentro de un handler ya elevado a <c>cae_app_aprovisionamiento</c>,
    /// rol que NO tiene GRANT sobre las tablas de plataforma que
    /// <c>RevalidarAsync</c> consulta — una tercera revalidación fresca aquí
    /// habría fallado con 42501 y roto la importación bajo Aprovisionamiento
    /// por completo. Este test blinda la corrección: <c>ObtenerAsync</c>
    /// (memoizado, sin tocar la base) es el único método que
    /// <c>EsActoDeAdministradorAsync</c> puede llamar.
    /// </summary>
    [Fact]
    public async Task Solo_llama_a_ObtenerAsync_memoizado_nunca_a_RevalidarAsync()
    {
        var tenant = Guid.NewGuid();
        var sesionInstrumentada = new SesionPrivilegiadaActualInstrumentada(
            SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenant));
        var servicio = new AutorizacionEscrituraEfectiva(
            new CurrentUserServiceFalso(Guid.NewGuid(), null), sesionInstrumentada, new TenantActualFalso(tenant));

        (await servicio.EsActoDeAdministradorAsync()).Should().BeTrue();

        sesionInstrumentada.VecesObtenerAsync.Should().Be(1);
        sesionInstrumentada.VecesRevalidarAsync.Should().Be(0,
            "una consulta a RevalidarAsync aquí fallaría por permisos bajo el rol de escritura acotada — " +
            "la memo que deja ElevacionEscrituraAprovisionamientoBehavior en ESTE MISMO comando ya basta");
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    private sealed class SesionPrivilegiadaActualInstrumentada(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public int VecesObtenerAsync { get; private set; }
        public int VecesRevalidarAsync { get; private set; }

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default)
        {
            VecesObtenerAsync++;
            return Task.FromResult(sesion);
        }

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default)
        {
            VecesRevalidarAsync++;
            return Task.FromResult(sesion);
        }
    }
}
