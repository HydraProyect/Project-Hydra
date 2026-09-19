using System.Reflection;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// Defecto medido en el log de servidor de un run de CI real (2026-09-18,
/// run 35402030148, artefacto <c>logs-caemanager-web-e2e</c>, descubierto
/// investigando — sin confirmarla — la hipótesis de #710 sobre
/// <c>SelectorTema</c>): tres componentes liberan su módulo de JS en
/// <c>DisposeAsync()</c> sin capturar <see cref="JSDisconnectedException"/>,
/// así que CUALQUIER desconexión de circuito con el módulo ya importado
/// (prácticamente todas: recarga, cierre de pestaña, navegación con reload)
/// lanza una excepción sin observar — "[ERR] Unhandled exception in
/// circuit" repetido decenas de veces en un único log de 124 KB.
/// <see cref="CaeManager.Web.Components.Layout.ExcepcionDeCircuitoDesconectado"/>
/// no sirve aquí: solo cubre carreras de Postgres/DbContext, no interop de
/// JS. El patrón correcto ya existe en el propio repositorio — copiado de
/// <c>TrazaSoporte.razor.cs</c> (<c>try { await _modulo.DisposeAsync(); }
/// catch (JSDisconnectedException) { }</c>) — y ya cubre 13 de los 16
/// componentes con un módulo de JS que liberar; estos tres eran la
/// excepción.
/// </summary>
public class DisposeAsyncDeModuloTrasCircuitoDesconectadoTests
{
    [Fact]
    public async Task SelectorTema_no_lanza_si_el_circuito_ya_se_desconecto_al_liberar_el_modulo()
    {
        var selectorTema = new SelectorTema();
        EscribirCampoPrivado(selectorTema, "_modulo", new ModuloQueLanzaAlDesconectar());

        var accion = async () => await selectorTema.DisposeAsync();

        await accion.Should().NotThrowAsync<JSDisconnectedException>(
            "el circuito ya desconectado es la razón por la que DisposeAsync se ejecuta, no un fallo");
    }

    [Fact]
    public async Task BotonCopiar_no_lanza_si_el_circuito_ya_se_desconecto_al_liberar_el_modulo()
    {
        var botonCopiar = new BotonCopiar();
        EscribirCampoPrivado(botonCopiar, "_modulo", new ModuloQueLanzaAlDesconectar());

        var accion = async () => await botonCopiar.DisposeAsync();

        await accion.Should().NotThrowAsync<JSDisconnectedException>(
            "el circuito ya desconectado es la razón por la que DisposeAsync se ejecuta, no un fallo");
    }

    [Fact]
    public async Task ContextWorkspace_no_lanza_si_el_circuito_ya_se_desconecto_al_liberar_el_modulo_de_portapapeles()
    {
        var contextWorkspace = new ContextWorkspace();
        EscribirPropiedadInyectada(contextWorkspace, "WorkspaceService", new ContextWorkspaceService());
        EscribirPropiedadInyectada(contextWorkspace, "Navigation", new NavegacionFalsa());
        EscribirCampoPrivado(contextWorkspace, "_moduloClipboard", new ModuloQueLanzaAlDesconectar());

        var accion = async () => await contextWorkspace.DisposeAsync();

        await accion.Should().NotThrowAsync<JSDisconnectedException>(
            "el circuito ya desconectado es la razón por la que DisposeAsync se ejecuta, no un fallo");
    }

    private static void EscribirCampoPrivado(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo privado '{nombre}' en {instancia.GetType().Name}."))
            .SetValue(instancia, valor);

    private static void EscribirPropiedadInyectada(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad inyectada '{nombre}' en {instancia.GetType().Name}."))
            .SetValue(instancia, valor);

    private sealed class ModuloQueLanzaAlDesconectar : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException("este falso solo se usa para DisposeAsync");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException("este falso solo se usa para DisposeAsync");

        public ValueTask DisposeAsync() =>
            throw new JSDisconnectedException(
                "simulado: el circuito ya se desconectó antes de que este módulo pudiera liberarse");
    }

    private sealed class NavegacionFalsa : NavigationManager
    {
        public NavegacionFalsa() => Initialize("http://localhost/", "http://localhost/inicio");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
