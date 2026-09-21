using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// La invalidación solo sirve si quien invalida es la MISMA instancia que memoiza. Con la composición
/// real (AddApplication + AddInfrastructure), dentro de un scope, el invalidador y el servicio de
/// alcance son el mismo objeto; en otro scope, otro distinto (la memoización sigue siendo por scope).
/// </summary>
public class InvalidadorAlcanceComposicionTests
{
    [Fact]
    public void El_invalidador_y_el_servicio_de_alcance_son_la_misma_instancia_dentro_de_un_scope()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CaeManagerDb"] = "Host=localhost;Database=no_se_conecta;Username=x;Password=x",
            ["ConnectionStrings:CaeManagerDbRuntime"] = "Host=localhost;Database=no_se_conecta;Username=x;Password=x",
        });
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
        // Las dependencias que aporta la capa Web y no Infrastructure.
        builder.Services.AddScoped<ITenantActual>(_ => new TenantActualAmbiental());
        builder.Services.AddScoped<IClienteActivoSeleccionado, SinSeleccion>();
        builder.Services.AddScoped<IActorAuditoria, ActorSinResolver>();
        builder.Services.AddScoped<ICurrentUserService>(_ => new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", Guid.NewGuid()));

        using var proveedor = builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
        using var scope = proveedor.CreateScope();
        using var otroScope = proveedor.CreateScope();

        var alcance = scope.ServiceProvider.GetRequiredService<IAlcanceDatosService>();
        var invalidador = scope.ServiceProvider.GetRequiredService<IInvalidadorAlcance>();

        alcance.Should().BeOfType<AlcanceDatosService>();
        invalidador.Should().BeSameAs(alcance);
        otroScope.ServiceProvider.GetRequiredService<IInvalidadorAlcance>().Should().NotBeSameAs(invalidador);
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ActorSinResolver : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.SinResolver);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.SinResolver;
    }
}
