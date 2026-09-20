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
using Microsoft.JSInterop;
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

    /// <summary>
    /// La segunda mitad del mismo defecto, y la que tumbó la PR #756 el
    /// 2026-09-20 (run 35512763740, merge sintético 856f9544): no basta con
    /// que el tema acabe guardándose — tiene que estar guardado <b>antes</b>
    /// de que el navegador lo vea aplicado.
    ///
    /// <para>
    /// El motivo es que <c>tema.js</c> no solo pinta <c>data-theme</c>: en la
    /// misma llamada escribe la cookie que <c>TemaCookie</c> (Web) lee al
    /// prerenderizar <c>App.razor</c>. Esa cookie es el compromiso visible de
    /// que la preferencia existe. Si se escribe mientras el <c>UPDATE</c>
    /// sigue en vuelo, una navegación inmediata sirve el HTML con el tema
    /// nuevo (desde la cookie) y acto seguido el circuito de la página nueva
    /// —que leyó <c>ApplicationUser.Tema</c> todavía sin actualizar— aplica
    /// el tema ANTERIOR, quita <c>data-theme</c> y borra la cookie. El
    /// usuario ve revertida la elección que acaba de hacer, y no hay ninguna
    /// excepción en ningún registro: las dos mitades funcionan, solo están
    /// en el orden equivocado.
    /// </para>
    ///
    /// <para>
    /// Medido así antes de corregirlo (worktree de origin/main, 2026-09-20):
    /// con un retardo de 5 s inyectado en <c>GuardarTemaAsync</c>, el E2E
    /// <c>SelectorTemaTests.El_tema_elegido_se_aplica_al_documento_y_sobrevive_a_la_recarga</c>
    /// falla con la firma exacta de CI —línea 56, <c>33 × locator resolved to
    /// &lt;html lang="en"&gt;</c>, «unexpected value null»— y la serie temporal
    /// del atributo muestra el HTML servido con <c>data-theme="oscuro"</c> y el
    /// DOM perdiéndolo 266 ms después. El retardo no inventa la carrera:
    /// ensancha una ventana que en CI vale ~250 ms (lo que tarda el test en
    /// navegar tras ver el tema aplicado).
    /// </para>
    ///
    /// <para>
    /// La invariante que fija este test es por tanto de orden, no de
    /// resultado: <b>la cookie nunca se adelanta a la cuenta</b>. Se observa
    /// en el único instante en que se puede observar —dentro de la propia
    /// llamada a <c>aplicarTema</c>— consultando la fila con un ámbito
    /// nuevo, que es exactamente lo que haría la petición HTTP de una
    /// navegación que llegara en ese momento.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_tema_no_llega_al_navegador_antes_de_estar_guardado_en_la_cuenta()
    {
        using var ambitoCircuito = _servicios.CreateScope();
        var selectorTema = CrearSelectorTema(ambitoCircuito.ServiceProvider, _usuarioId);
        await InvocarOnInitializedAsync(selectorTema);

        // El módulo JS ya importado: es el estado normal cuando el usuario
        // toca el selector (OnAfterRenderAsync lo importa al conectar el
        // circuito, mucho antes). Con _modulo en null, CambiarTemaAsync no
        // llegaría a aplicar nada y este test no observaría el orden.
        var modulo = new ModuloQueMiraLaCuentaAlAplicar(_servicios, _usuarioId);
        EscribirCampoPrivado(selectorTema, "_modulo", modulo);

        await InvocarCambiarTemaAsync(selectorTema, "oscuro");

        modulo.TemasAplicados.Should().Equal(["oscuro"],
            "sin esta comprobación previa, un fallo en la de abajo no distinguiría «se aplicó demasiado " +
            "pronto» de «no se aplicó nunca»");

        modulo.TemaEnLaCuentaAlAplicar.Should().Equal([TemaPreferido.Oscuro],
            "cuando tema.js escribe la cookie que TemaCookie leerá en la siguiente petición, el UPDATE de " +
            "ApplicationUser.Tema ya tiene que haber cuajado: si no, una navegación inmediata sirve el HTML " +
            "con el tema nuevo y el circuito de esa página lo revierte al leer la fila sin actualizar — el " +
            "fallo que expulsó a la PR #756 de la cola de fusión");
    }

    /// <summary>
    /// Segunda refutación de Codex (2026-09-20) a la corrección de orden: la
    /// primera versión solo garantizaba «guardar intentado antes de aplicar».
    /// <c>GuardarTemaAsync</c> sale con normalidad —tras registrar el fallo—
    /// cuando la preferencia NO quedó persistida, y aplicar igualmente el tema
    /// escribía la cookie de una elección que la cuenta no tiene: la misma
    /// carrera, con otra causa.
    ///
    /// <para>
    /// Se fuerza el caso de recarga nula, que es el fallo persistente más
    /// simple de construir sin tocar el componente: la cuenta desaparece entre
    /// la carga del selector y el guardado. <c>UpdateAsync</c> afecta a cero
    /// filas → <c>ConcurrencyFailure</c>; <c>FindByIdAsync</c> devuelve
    /// <c>null</c>; no hay nada que reintentar. Los otros dos caminos que
    /// salían sin persistir (error de Identity no concurrente, y fallo del
    /// reintento) pasan por el mismo <c>return false</c> y no se duplican
    /// aquí: construirlos exigiría un doble de <c>UserManager</c> que
    /// desconectaría el test de lo que de verdad hace Identity.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_tema_no_se_aplica_en_el_navegador_si_no_pudo_guardarse_en_la_cuenta()
    {
        using var ambitoCircuito = _servicios.CreateScope();
        var selectorTema = CrearSelectorTema(ambitoCircuito.ServiceProvider, _usuarioId);
        await InvocarOnInitializedAsync(selectorTema);

        var modulo = new ModuloQueMiraLaCuentaAlAplicar(_servicios, _usuarioId);
        EscribirCampoPrivado(selectorTema, "_modulo", modulo);

        using (var ambitoBorrado = _servicios.CreateScope())
        {
            var userManagerBorrado = ambitoBorrado.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuarioABorrar = await userManagerBorrado.FindByIdAsync(_usuarioId.ToString());
            (await userManagerBorrado.DeleteAsync(usuarioABorrar!)).Succeeded.Should().BeTrue(
                "el borrado es la condición del test, no lo que se mide");
        }

        await InvocarCambiarTemaAsync(selectorTema, "oscuro");

        modulo.TemasAplicados.Should().BeEmpty(
            "aplicar el tema escribe la cookie que TemaCookie lee en la siguiente petición: con el guardado " +
            "fallido —cuenta inexistente al recargar tras el conflicto— esa cookie apuntaría a una preferencia " +
            "que la cuenta no tiene, y el circuito de la página siguiente la revertiría");
    }

    /// <summary>
    /// Tercera observación de la revisión de Codex: con dos cambios solapados,
    /// el primero se aplicaba al cuajar su guardado aunque <c>_temaActual</c>
    /// ya fuera el segundo, que seguía esperando el suyo — un estado visual
    /// obsoleto y una cookie adelantada al guardado vigente. El solapamiento
    /// es el del propio código: el primer <c>CambiarTemaAsync</c> cede el
    /// control en su primer <c>await</c> con E/S real contra Postgres, y el
    /// segundo arranca —y fija <c>_temaActual</c>— antes de que aquel
    /// vuelva. No hay retardos ni sincronización artificial; que el orden sea
    /// ese lo comprueba la propia aserción (si no se solaparan, se aplicarían
    /// dos temas y la lista no sería <c>["claro"]</c>).
    /// </summary>
    [Fact]
    public async Task Con_dos_cambios_solapados_solo_se_aplica_el_vigente_cuando_cuaja_su_guardado()
    {
        using var ambitoCircuito = _servicios.CreateScope();
        var selectorTema = CrearSelectorTema(ambitoCircuito.ServiceProvider, _usuarioId);
        await InvocarOnInitializedAsync(selectorTema);

        var modulo = new ModuloQueMiraLaCuentaAlAplicar(_servicios, _usuarioId);
        EscribirCampoPrivado(selectorTema, "_modulo", modulo);

        // El segundo cambio se lanza SIN esperar al primero: así, cuando el
        // primero termina su guardado, _temaActual ya es "claro".
        var primero = InvocarCambiarTemaAsync(selectorTema, "oscuro");
        var segundo = InvocarCambiarTemaAsync(selectorTema, "claro");
        await Task.WhenAll(primero, segundo).WaitAsync(TimeSpan.FromSeconds(30));

        modulo.TemasAplicados.Should().Equal(["claro"],
            "el primer cambio ya no era el vigente cuando su guardado cuajó: aplicarlo pintaría un tema " +
            "obsoleto y escribiría una cookie que no es la de la elección actual");
        modulo.TemaEnLaCuentaAlAplicar.Should().Equal([TemaPreferido.Claro],
            "y el que sí se aplica tiene que estar ya en la cuenta");
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

    private static void EscribirCampoPrivado(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo privado '{nombre}'."))
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

    /// <summary>
    /// Doble del módulo <c>wwwroot/js/tema.js</c> que, en el momento exacto en
    /// que el componente le pide aplicar un tema, va a la base con un ámbito
    /// NUEVO y anota qué tema tiene la cuenta en ese instante.
    ///
    /// <para>
    /// El ámbito nuevo no es un detalle: reusar el del circuito leería la
    /// entidad ya trackeada (mapa de identidad de EF) y respondería con el
    /// valor en memoria, no con el de la fila — daría verde siempre, y sería
    /// un instrumento incapaz de observar la propiedad que este test afirma
    /// medir. Un ámbito nuevo es además lo que de verdad hay al otro lado:
    /// la petición HTTP de la navegación siguiente.
    /// </para>
    /// </summary>
    private sealed class ModuloQueMiraLaCuentaAlAplicar(IServiceProvider servicios, Guid usuarioId) : IJSObjectReference
    {
        public List<string> TemasAplicados { get; } = [];

        public List<TemaPreferido> TemaEnLaCuentaAlAplicar { get; } = [];

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "aplicarTema")
            {
                TemasAplicados.Add((string)args![0]!);

                using var ambito = servicios.CreateScope();
                var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var usuario = await userManager.FindByIdAsync(usuarioId.ToString());

                // Cuenta inexistente (test del guardado fallido): no hay tema
                // que anotar, y NO se lanza — un NullReferenceException aquí
                // haría caer la aserción del test por un motivo distinto del
                // que mide (medido al aplicar la mutación de ese test: rojo
                // por NRE del doble, no por «se aplicó sin guardar»).
                if (usuario is not null)
                    TemaEnLaCuentaAlAplicar.Add(usuario.Tema);
            }

            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SinTenantActual : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
