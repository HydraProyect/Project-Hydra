using CaeManager.Application.Common;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class GateComercialTenantBehaviorTests
{
    private record FalsoCommand : ICommand;
    private record FalsoConValorCommand : ICommand<Guid>;
    private record FalsaQuery : IRequest<string>;

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private static (Tenant tenant, TenantsQueryContextFalso dbContext) CrearTenantConEstado(EstadoComercialTenant estado)
    {
        var tenant = new Tenant("Ibertec");
        if (estado != EstadoComercialTenant.SinSuscripcion)
        {
            tenant.VincularSuscripcionStripe("cus_1", "sub_1", EstadoComercialTenant.Activa);
            if (estado != EstadoComercialTenant.Activa)
                tenant.ActualizarEstadoComercial(estado);
        }

        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(tenant);
        return (tenant, dbContext);
    }

    [Fact]
    public async Task No_bloquea_una_query()
    {
        var (_, dbContext) = CrearTenantConEstado(EstadoComercialTenant.Suspendida);
        var tenant = dbContext.ListaTenants[0];
        var behavior = new GateComercialTenantBehavior<FalsaQuery, string>(new TenantActualFalso(tenant.Id), dbContext);

        var resultado = await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
    }

    [Fact]
    public async Task No_bloquea_si_no_hay_tenant_resuelto_trabajos_de_fondo_seeds()
    {
        var dbContext = new TenantsQueryContextFalso();
        var behavior = new GateComercialTenantBehavior<FalsoCommand, Result>(new TenantActualFalso(null), dbContext);

        var resultado = await behavior.Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// <b>Este test no observaba lo que su nombre afirma.</b> Construía el tenant con
    /// <c>new Tenant("TALVEG")</c> y lo marcaba como plataforma, pero <b>nunca tocaba
    /// <c>EstadoComercial</c></b>, que arranca en <c>SinSuscripcion</c> — un estado que el
    /// gate ya deja pasar por su caso por defecto, exista o no la exención. Seguía verde
    /// aunque se borrara <c>|| tenant.EsPlataforma</c>: afirmaba una propiedad que no podía
    /// ver.
    ///
    /// <para>
    /// Ahora suspende de verdad, con el mismo helper que el resto de la clase. Consecuencia
    /// deliberada: cuando se ejecute D-4 (d) y se retire la exención, <b>este test se
    /// pondrá rojo</b>, y ese rojo es correcto — significa que el comportamiento cambió, que
    /// es exactamente lo que D-4 pedía poder observar antes de tocar nada.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_bloquea_al_tenant_de_plataforma_aunque_este_suspendido()
    {
        var (tenant, dbContext) = CrearTenantConEstado(EstadoComercialTenant.Suspendida);
        tenant.MarcarComoPlataforma();

        var behavior = new GateComercialTenantBehavior<FalsoCommand, Result>(new TenantActualFalso(tenant.Id), dbContext);

        var resultado = await behavior.Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoComercialTenant.SinSuscripcion)]
    [InlineData(EstadoComercialTenant.Activa)]
    [InlineData(EstadoComercialTenant.AvisoImpago)]
    public async Task No_bloquea_las_etapas_anteriores_a_solo_lectura(EstadoComercialTenant estado)
    {
        var (tenant, dbContext) = CrearTenantConEstado(estado);
        var behavior = new GateComercialTenantBehavior<FalsoCommand, Result>(new TenantActualFalso(tenant.Id), dbContext);

        var resultado = await behavior.Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Bloquea_un_command_con_resultado_simple_en_solo_lectura()
    {
        var (tenant, dbContext) = CrearTenantConEstado(EstadoComercialTenant.SoloLectura);
        var behavior = new GateComercialTenantBehavior<FalsoCommand, Result>(new TenantActualFalso(tenant.Id), dbContext);
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoCommand(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.SoloLectura");
        siguienteFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task Bloquea_un_command_con_resultado_generico_en_suspendido()
    {
        var (tenant, dbContext) = CrearTenantConEstado(EstadoComercialTenant.Suspendida);
        var behavior = new GateComercialTenantBehavior<FalsoConValorCommand, Result<Guid>>(new TenantActualFalso(tenant.Id), dbContext);

        var resultado = await behavior.Handle(
            new FalsoConValorCommand(), _ => Task.FromResult(Result.Exito(Guid.NewGuid())), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.Suspendido");
    }
}
