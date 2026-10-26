using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Señales del aviso de la vía heredada de soporte (<see cref="TrazaSoporte"/>, #1686): <c>data-captura-lista</c> dice que
/// el listener de clics de <c>trazaSoporte.js</c> ya está enganchado y <c>data-interacciones-registradas</c> cuenta las
/// interacciones ya guardadas. El E2E <c>FlujoSoporteTests</c> espera a ellas antes de cambiar de Tenant, porque esa
/// recarga destruye el circuito y con él el lote pendiente.
///
/// <para>Lo que se fija aquí es lo que el E2E no distingue: el contador sube solo <b>después</b> de que el registro se
/// haya guardado (si subiera antes, el E2E podría recargar con el lote todavía sin guardar), cada interacción queda a
/// nombre del usuario autenticado del circuito, que es el Actor real, y las señales no llevan el texto de lo pulsado.</para>
/// </summary>
public class TrazaSoporteSenalesDeCapturaTests : BunitContext
{
    private static readonly Guid ActorReal = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Plataforma = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a0");
    private static readonly Guid Visitado = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000b0");

    private readonly Registros _registros = new();
    private readonly GuardadoRetenible _guardado = new();
    private readonly DelegacionTenant _delegacion;
    private readonly BunitJSModuleInterop _modulo;
    private readonly BunitJSModuleInterop _captura;

    public TrazaSoporteSenalesDeCapturaTests()
    {
        _delegacion = DelegacionTenant.ParaSoporte(Plataforma, Visitado);
        _delegacion.ActivarParaSoporte("Incidencia de prueba", DateTime.UtcNow.AddHours(4), DateTime.UtcNow);

        Services.AddLocalization();
        Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionHeredada());
        Services.AddSingleton(new TrazaSoporteService(
            new SeleccionHeredada(),
            new UsuarioAutenticado(),
            new ConsultaTenants(_delegacion, new AsignacionOperadorDelegado(_delegacion.Id, ActorReal, "Administrador")),
            _registros,
            _guardado,
            new PuertaAccesoDatos()));

        _modulo = JSInterop.SetupModule("./js/trazaSoporte.js");
        // iniciar devuelve el manejador de la captura de ese montaje; detener se llama sobre él, no sobre el módulo.
        _captura = _modulo.SetupModule(invocacion => invocacion.Identifier == "iniciar");
        _captura.SetupVoid("detener").SetVoidResult();
    }

    [Fact]
    public async Task Dos_renders_durante_la_importacion_enganchan_una_sola_vez()
    {
        var retenido = new ImportacionRetenida(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(retenido);
        var aviso = Render<TrazaSoporte>();
        aviso.Render();
        aviso.Render();
        retenido.Veces.Should().Be(1);
        _modulo.VerifyNotInvoke("iniciar");

        retenido.Soltar();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));
        _modulo.VerifyInvoke("iniciar", 1);
        await DisposeComponentsAsync();
        _captura.VerifyInvoke("detener", 1);
    }

    [Fact]
    public async Task Desmontar_desengancha_y_volver_a_montar_inicia_otra_captura()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() => _modulo.VerifyInvoke("iniciar", 1));
        await DisposeComponentsAsync();
        _captura.VerifyInvoke("detener", 1);

        var nuevo = Render<TrazaSoporte>();
        nuevo.WaitForAssertion(() => _modulo.VerifyInvoke("iniciar", 2));
        await DisposeComponentsAsync();
        _captura.VerifyInvoke("detener", 2);
    }

    [Fact]
    public async Task Desmontar_espera_a_que_el_lote_que_vacia_detener_quede_guardado()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));
        var referencia = (DotNetObjectReference<TrazaSoporte>)_modulo.Invocations["iniciar"].Single().Arguments[0]!;
        var detener = _captura.SetupVoid("detener");

        var desmontaje = DisposeComponentsAsync();

        // detener() en vuelo es el lote pendiente camino del servidor: la referencia sigue viva para recibirlo.
        _captura.VerifyInvoke("detener", 1);
        desmontaje.IsCompleted.Should().BeFalse("el desmontaje no puede adelantarse al envío del último lote");
        await referencia.Value.RegistrarInteraccionesAsync(["Guardar — /clientes"]);
        _registros.Lista.Should().ContainSingle(r => r.Tipo == TipoActividadSoporte.Interaccion && r.Detalle == "Guardar — /clientes");

        detener.SetVoidResult();
        await desmontaje;
        var liberada = () => referencia.Value;
        liberada.Should().Throw<ObjectDisposedException>("la referencia se libera solo después de vaciar el lote");
    }

    [Theory]
    [InlineData("cancelado")]
    [InlineData("js")]
    [InlineData("desconectado")]
    public async Task Si_detener_falla_el_desmontaje_no_lanza_y_libera_la_referencia(string fallo)
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));
        var referencia = (DotNetObjectReference<TrazaSoporte>)_modulo.Invocations["iniciar"].Single().Arguments[0]!;
        var detener = _captura.SetupVoid("detener");
        switch (fallo)
        {
            case "cancelado": detener.SetCanceled(); break;
            case "js": detener.SetException(new JSException("El navegador no pudo detener la captura.")); break;
            default: detener.SetException(new JSDisconnectedException("Circuito cerrado.")); break;
        }

        var desmontar = DisposeComponentsAsync;

        await desmontar.Should().NotThrowAsync("un navegador caído o lento no puede derribar el desmontaje del circuito");
        var liberada = () => referencia.Value;
        liberada.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Detener_lleva_un_tope_para_no_retener_el_desmontaje()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() => _modulo.VerifyInvoke("iniciar", 1));

        await DisposeComponentsAsync();

        (_captura.Invocations["detener"].Single().CancellationToken ?? CancellationToken.None).CanBeCanceled.Should()
            .BeTrue("un navegador que no contesta no puede retener el desmontaje del circuito");
    }

    [Fact]
    public async Task Si_liberar_la_captura_falla_el_modulo_se_libera_igual()
    {
        var espia = new ModuloEspiado(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(espia);
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));
        var referencia = (DotNetObjectReference<TrazaSoporte>)_modulo.Invocations["iniciar"].Single().Arguments[0]!;

        var desmontar = DisposeComponentsAsync;

        await desmontar.Should().NotThrowAsync();
        _captura.VerifyInvoke("detener", 1);
        espia.ModuloLiberado.Should().BeTrue("cada referencia JS se libera aunque falle la anterior");
        var liberada = () => referencia.Value;
        liberada.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>Envuelve el módulo importado para saber si se libera, con una captura que falla al liberarse.</summary>
    private sealed class ModuloEspiado(IJSRuntime interno) : IJSRuntime
    {
        public bool ModuloLiberado { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var resultado = await interno.InvokeAsync<TValue>(identifier, cancellationToken, args);
            return identifier == "import"
                ? (TValue)(object)new Modulo((IJSObjectReference)resultado!, () => ModuloLiberado = true)
                : resultado;
        }

        private sealed class Modulo(IJSObjectReference interno, Action alLiberar) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                InvokeAsync<TValue>(identifier, CancellationToken.None, args);
            public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                var resultado = await interno.InvokeAsync<TValue>(identifier, cancellationToken, args);
                return identifier == "iniciar"
                    ? (TValue)(object)new CapturaQueFallaAlLiberar((IJSObjectReference)resultado!)
                    : resultado;
            }
            public ValueTask DisposeAsync()
            {
                alLiberar();
                return interno.DisposeAsync();
            }
        }

        private sealed class CapturaQueFallaAlLiberar(IJSObjectReference interno) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                interno.InvokeAsync<TValue>(identifier, args);
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
                interno.InvokeAsync<TValue>(identifier, cancellationToken, args);
            public ValueTask DisposeAsync() => throw new JSException("El navegador no pudo liberar la captura.");
        }
    }

    [Fact]
    public async Task Desmontar_con_la_importacion_en_vuelo_no_engancha_nada()
    {
        var retenido = new ImportacionRetenida(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(retenido);
        Render<TrazaSoporte>();
        retenido.Veces.Should().Be(1);

        var desmontaje = DisposeComponentsAsync();
        retenido.Soltar();
        await desmontaje;

        _modulo.VerifyNotInvoke("iniciar");
        _captura.VerifyNotInvoke("detener");
    }

    private sealed class ImportacionRetenida(IJSRuntime interno) : IJSRuntime
    {
        private readonly TaskCompletionSource _retencion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Veces { get; private set; }
        public void Soltar() => _retencion.TrySetResult();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                Veces++;
                await _retencion.Task;
            }
            return await interno.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }
    }

    [Fact]
    public void El_aviso_marca_la_captura_lista_solo_tras_enganchar_el_listener()
    {
        var aviso = Render<TrazaSoporte>();

        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));
        JSInterop.VerifyInvoke("import");
    }

    [Fact]
    public async Task El_contador_sube_despues_de_guardar_y_la_interaccion_queda_a_nombre_del_usuario_autenticado()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));
        _registros.Lista.Should().ContainSingle(r => r.Tipo == TipoActividadSoporte.Navegacion,
            "la primera pantalla se anota al inicializar");

        const string pulsado = "Empresas — /clientes";
        _guardado.Retener();
        var lote = aviso.InvokeAsync(() => aviso.Instance.RegistrarInteraccionesAsync([pulsado]));

        // Con el guardado en vuelo el lote aún no está persistido: la señal no puede adelantarse.
        aviso.Find(".aviso-sesion-soporte").GetAttribute("data-interacciones-registradas").Should().Be("0");

        _guardado.Soltar();
        await lote;

        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-interacciones-registradas").Should().Be("1"));
        var interaccion = _registros.Lista.Should().ContainSingle(r => r.Tipo == TipoActividadSoporte.Interaccion).Subject;
        interaccion.UsuarioSoporteId.Should().Be(ActorReal);
        interaccion.DelegacionTenantId.Should().Be(_delegacion.Id);
        interaccion.Detalle.Should().Be(pulsado);
        _guardado.Veces.Should().Be(2, "una vez la navegación inicial y otra la interacción");

        var atributos = aviso.Find(".aviso-sesion-soporte").Attributes
            .Where(a => a.Name.StartsWith("data-", StringComparison.Ordinal))
            .ToDictionary(a => a.Name, a => a.Value);
        atributos.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["data-aviso-sistema"] = "",
            ["data-tono"] = "soporte",
            ["data-captura-lista"] = "true",
            ["data-interacciones-registradas"] = "1",
        }, "las señales del aviso son un booleano y un recuento, nunca lo que se pulsó ni dónde");
        aviso.Find(".aviso-sesion-soporte").OuterHtml.Should().NotContain(pulsado).And.NotContain("/clientes");
    }

    [Fact]
    public async Task Un_lote_de_varias_interacciones_suma_todas()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));

        await aviso.InvokeAsync(() => aviso.Instance.RegistrarInteraccionesAsync(["Uno — /a", "Dos — /b", "Tres — /c"]));

        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-interacciones-registradas").Should().Be("3"));
        _registros.Lista.Where(r => r.Tipo == TipoActividadSoporte.Interaccion).Select(r => r.Detalle)
            .Should().Equal("Uno — /a", "Dos — /b", "Tres — /c");
    }

    [Fact]
    public async Task Si_el_guardado_falla_el_contador_no_se_mueve()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));

        _guardado.FallarDesdeAhora();
        var lote = () => aviso.InvokeAsync(() => aviso.Instance.RegistrarInteraccionesAsync(["Guardar — /clientes"]));

        await lote.Should().ThrowAsync<InvalidOperationException>(
            "el fallo llega a trazaSoporte.js, que lo descarta; el aviso no puede darlo por guardado");
        aviso.Find(".aviso-sesion-soporte").GetAttribute("data-interacciones-registradas").Should().Be("0");
    }

    [Fact]
    public async Task Un_lote_vacio_no_mueve_el_contador()
    {
        var aviso = Render<TrazaSoporte>();
        aviso.WaitForAssertion(() =>
            aviso.Find(".aviso-sesion-soporte").GetAttribute("data-captura-lista").Should().Be("true"));

        await aviso.InvokeAsync(() => aviso.Instance.RegistrarInteraccionesAsync([]));

        aviso.Find(".aviso-sesion-soporte").GetAttribute("data-interacciones-registradas").Should().Be("0");
        _registros.Lista.Should().NotContain(r => r.Tipo == TipoActividadSoporte.Interaccion);
    }

    private sealed class SeleccionHeredada : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => Visitado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class UsuarioAutenticado : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(ActorReal);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Administrador");
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Plataforma);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class Registros : IRegistroActividadSoporteRepository
    {
        public List<RegistroActividadSoporte> Lista { get; } = [];
        public void Agregar(RegistroActividadSoporte registro) => Lista.Add(registro);
        public Task<IReadOnlyList<RegistroActividadSoporte>> ObtenerPorDelegacionAsync(
            Guid delegacionTenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RegistroActividadSoporte>>(Lista);
    }

    /// <summary>Guardado que puede quedarse en vuelo, para observar el aviso antes de que el lote quede persistido.</summary>
    private sealed class GuardadoRetenible : IUnitOfWork
    {
        private TaskCompletionSource? _retencion;
        public int Veces { get; private set; }

        public void Retener() => _retencion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Soltar() => _retencion?.TrySetResult();
        public void FallarDesdeAhora() => _falla = true;
        private bool _falla;

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_retencion is not null) await _retencion.Task;
            if (_falla) throw new InvalidOperationException("La base no aceptó el registro.");
            Veces++;
            return 1;
        }
    }

    private sealed class ConsultaTenants(DelegacionTenant delegacion, AsignacionOperadorDelegado asignacion) : ITenantsQueryContext
    {
        public IQueryable<Tenant> Tenants => Consulta(Array.Empty<Tenant>());
        public IQueryable<DelegacionTenant> DelegacionesTenant => Consulta([delegacion]);
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Consulta([asignacion]);
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => Consulta(Array.Empty<RegistroActividadSoporte>());
    }

    private static IQueryable<T> Consulta<T>(IEnumerable<T> datos) => new ConsultaAsincrona<T>(datos.AsQueryable());

    private sealed class ConsultaAsincrona<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
    {
        public Type ElementType => typeof(T);
        public Expression Expression => interna.Expression;
        public IQueryProvider Provider => new ProveedorAsincrono(interna.Provider);
        public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new EnumeradorAsincrono<T>(GetEnumerator());
    }

    private sealed class ProveedorAsincrono(IQueryProvider interno) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new ConsultaAsincrona<TElement>(interno.CreateQuery<TElement>(expression));
        public object? Execute(Expression expression) => interno.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var tipoResultado = typeof(TResult).GetGenericArguments()[0];
            var resultado = interno.Execute(expression);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado).Invoke(null, [resultado])!;
        }
    }

    private sealed class EnumeradorAsincrono<T>(IEnumerator<T> interno) : IAsyncEnumerator<T>
    {
        public T Current => interno.Current;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());
        public ValueTask DisposeAsync() { interno.Dispose(); return ValueTask.CompletedTask; }
    }
}
