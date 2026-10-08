using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La señal «actúa por Encargo de administración» (decisión D-8, 2026-10-08) la da <b>la misma
/// instancia</b> que resuelve el rol efectivo. Si el contenedor las resolviera a objetos distintos, o
/// la aplicación web cayera al doble inerte, el rol se elevaría sin que nadie lo supiera: ni las
/// exclusiones, ni la auditoría, ni la revalidación del circuito.
/// </summary>
public class EncargoDeAdministracionEnElContenedorTests
{
    [Fact]
    public void El_servicio_de_usuario_actual_de_la_aplicacion_web_da_la_senal_del_encargo()
    {
        typeof(IEncargoDeAdministracionActual).IsAssignableFrom(typeof(CurrentUserService)).Should().BeTrue(
            "AddApplication reenvía IEncargoDeAdministracionActual al ICurrentUserService registrado; si "
            + "CurrentUserService dejara de implementarla, la aplicación web caería a SinEncargoDeAdministracion "
            + "y el rol elevado dejaría de tener exclusiones");

        var programa = File.ReadAllText(Path.Combine(Raiz(), "src", "CaeManager.Web", "Program.cs"));
        programa.Should().Contain("AddScoped<ICurrentUserService, CurrentUserService>()",
            "la aplicación web registra CurrentUserService tal cual, sin un envoltorio que escondería la señal");
    }

    [Fact]
    public void El_contenedor_resuelve_la_senal_a_la_misma_instancia_que_el_usuario_actual()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddScoped<ICurrentUserService, UsuarioQueModelaElEncargo>();
        using var proveedor = servicios.BuildServiceProvider();
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IEncargoDeAdministracionActual>()
            .Should().BeSameAs(ambito.ServiceProvider.GetRequiredService<ICurrentUserService>());
    }

    [Fact]
    public void Un_usuario_actual_que_no_modela_el_encargo_no_eleva_nunca()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddScoped<ICurrentUserService, UsuarioQueNoModelaElEncargo>();
        using var proveedor = servicios.BuildServiceProvider();
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IEncargoDeAdministracionActual>()
            .Should().BeSameAs(SinEncargoDeAdministracion.Instancia);
    }

    [Fact]
    public void Las_exclusiones_del_encargo_corren_despues_de_la_autorizacion_de_escritura()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        var behaviors = servicios
            .Where(d => d.ServiceType == typeof(MediatR.IPipelineBehavior<,>))
            .Select(d => d.ImplementationType)
            .ToList();

        var escritura = behaviors.IndexOf(typeof(AutorizacionEscrituraBehavior<,>));
        escritura.Should().BeGreaterThanOrEqualTo(0);
        behaviors.IndexOf(typeof(ExclusionesDelEncargoBehavior<,>)).Should().BeGreaterThan(escritura,
            "la autorización de escritura acaba de resolver el rol efectivo; y quien no tiene rol de escritura "
            + "recibe ese rechazo, no el del encargo");
    }

    private class UsuarioQueNoModelaElEncargo : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    private sealed class UsuarioQueModelaElEncargo : UsuarioQueNoModelaElEncargo, IEncargoDeAdministracionActual
    {
        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult<Guid?>(null);
        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => null;
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
               ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
