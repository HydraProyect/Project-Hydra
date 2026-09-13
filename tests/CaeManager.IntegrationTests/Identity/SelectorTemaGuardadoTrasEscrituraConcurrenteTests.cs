using System.Reflection;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// Reproduce en aislamiento el hallazgo de
/// <c>SelectorTemaTests.El_tema_elegido_se_aplica_al_documento_y_sobrevive_a_la_recarga</c>
/// (CI 2026-09-12, runs 34704233652 y 34712092934): <c>SelectorTema</c> carga
/// <c>ApplicationUser</c> una sola vez en <c>OnInitializedAsync</c> y lo guarda
/// mucho después, cuando el usuario por fin toca el selector. Si entre medias
/// otra escritura sobre la misma cuenta —<c>ActividadUsuarioService</c> toca
/// <c>UltimaActividadUtc</c> en cada carga de página, con su propio
/// <c>UserManager.UpdateAsync</c>, ver <c>ApplicationUser.UltimaActividadUtc</c>—
/// renueva <c>ConcurrencyStamp</c>, el guardado del tema con la entidad
/// obsoleta falla: <c>UserStore</c> de Identity atrapa
/// <c>DbUpdateConcurrencyException</c> y devuelve un <see cref="IdentityResult"/>
/// fallido sin lanzar. La versión anterior de <c>CambiarTemaAsync</c>
/// descartaba ese resultado — la preferencia se perdía en silencio y una
/// recarga posterior (nuevo circuito, nueva lectura) revertía al tema previo,
/// justo el síntoma que capturó el E2E.
///
/// <para>
/// Por qué no bUnit: no hace falta renderer — <c>CambiarTemaAsync</c> no llama
/// a <c>StateHasChanged</c> ni depende del árbol de renderizado, solo de
/// <c>UserManager</c> real (para que <c>ConcurrencyStamp</c> se comporte como
/// en producción) y del propio flujo de datos. Mismo patrón que
/// <c>FronteraDeTenantEnGestionDeUsuariosTests</c>: se instancia el componente
/// real, se inyectan sus dependencias por reflexión y se invoca el método
/// privado bajo prueba.
/// </para>
/// </summary>
public class SelectorTemaGuardadoTrasEscrituraConcurrenteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private ServiceProvider _servicios = null!;
    private Guid _usuarioId;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        // AspNetUsers no lleva filtro global de tenant (ver ApplicationUser.TenantId),
        // pero CaeManagerDbContext exige la dependencia igual para el resto de
        // entidades — ninguno de los caminos bajo prueba la consulta.
        servicios.AddSingleton<ITenantActual>(new SinTenantActual());

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddScoped<IDesenganchadorDeEntidadesRastreadas>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<CaeManagerDbContext>();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = "administrador-consultora@x.test",
            Email = "administrador-consultora@x.test",
            NombreCompleto = "Administrador Consultora",
            TenantId = Guid.NewGuid(),
        };
        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        _usuarioId = usuario.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_tema_elegido_se_guarda_aunque_otra_escritura_haya_renovado_ConcurrencyStamp_entre_medias()
    {
        using var ambitoCircuito = _servicios.CreateScope();
        var selectorTema = CrearSelectorTema(ambitoCircuito.ServiceProvider, _usuarioId);

        // Arranca el componente: carga _usuario una vez, como en un circuito
        // real recién conectado.
        await InvocarOnInitializedAsync(selectorTema);

        // La escritura concurrente que dispara el hallazgo: otra parte de la
        // misma app (en producción, ActividadUsuarioService desde MainLayout)
        // toca la cuenta con SU PROPIO UserManager, renovando
        // ConcurrencyStamp en la base. El _usuario en memoria del selector no
        // se entera.
        using (var ambitoActividad = _servicios.CreateScope())
        {
            var userManagerActividad = ambitoActividad.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuarioParaTocar = await userManagerActividad.FindByIdAsync(_usuarioId.ToString());
            usuarioParaTocar!.UltimaActividadUtc = DateTime.UtcNow;
            (await userManagerActividad.UpdateAsync(usuarioParaTocar)).Succeeded.Should().BeTrue(
                "la propia escritura de 'actividad' que provoca la carrera tiene que guardarse sin problema");
        }

        // El usuario elige "oscuro" en un selector cuyo _usuario ya está
        // obsoleto frente a la fila real.
        await InvocarCambiarTemaAsync(selectorTema, "oscuro");

        using var ambitoVerificacion = _servicios.CreateScope();
        var userManagerVerificacion = ambitoVerificacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuarioTrasElCambio = await userManagerVerificacion.FindByIdAsync(_usuarioId.ToString());

        usuarioTrasElCambio!.Tema.Should().Be(TemaPreferido.Oscuro,
            "una escritura concurrente sobre la misma cuenta entre la carga del selector y el guardado del " +
            "tema elegido no debe hacer que la preferencia se pierda en silencio");
    }

    private static SelectorTema CrearSelectorTema(IServiceProvider servicios, Guid usuarioId)
    {
        var selectorTema = new SelectorTema();

        EscribirPropiedadInyectada(selectorTema, "UserManager", servicios.GetRequiredService<UserManager<ApplicationUser>>());
        EscribirPropiedadInyectada(selectorTema, "PuertaAccesoDatos", servicios.GetRequiredService<PuertaAccesoDatos>());
        EscribirPropiedadInyectada(selectorTema, "Desenganchador", servicios.GetRequiredService<IDesenganchadorDeEntidadesRastreadas>());
        EscribirPropiedadInyectada(selectorTema, "Logger", servicios.GetRequiredService<ILogger<SelectorTema>>());
        EscribirPropiedadInyectada(selectorTema, "AuthenticationStateProvider", new AutenticacionFalsa(usuarioId));

        return selectorTema;
    }

    private static void EscribirPropiedadInyectada(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad inyectada '{nombre}'."))
            .SetValue(instancia, valor);

    // ── Reflexión: OnInitializedAsync y CambiarTemaAsync son protegido/privado
    // a propósito. Invocarlos así ejercita el código de producción tal cual,
    // sin bUnit y sin necesitar un RenderHandle real — ninguno de los dos
    // llama a StateHasChanged.

    private static async Task InvocarOnInitializedAsync(SelectorTema selectorTema)
    {
        var mi = typeof(SelectorTema).GetMethod(
            "OnInitializedAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("No se encontró OnInitializedAsync.");
        await (Task)mi.Invoke(selectorTema, null)!;
    }

    private static async Task InvocarCambiarTemaAsync(SelectorTema selectorTema, string tema)
    {
        var mi = typeof(SelectorTema).GetMethod(
            "CambiarTemaAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("No se encontró CambiarTemaAsync.");
        await (Task)mi.Invoke(selectorTema, [new ChangeEventArgs { Value = tema }])!;
    }

    private sealed class AutenticacionFalsa(Guid usuarioId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, usuarioId.ToString()) };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba"));
            return Task.FromResult(new AuthenticationState(principal));
        }
    }

    private sealed class SinTenantActual : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
