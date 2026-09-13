using Bunit;
using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.ApiKeys.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ClavesApiGen2Tests : BunitContext
{
    public ClavesApiGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;
    private sealed class Mediador : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];
        public TaskCompletionSource? EsperaRevocacion { get; set; }
        public Queue<TaskCompletionSource> EsperasGeneracion { get; } = [];
        public Queue<string> ClavesGeneradas { get; } = [];
        public IReadOnlyList<DelegacionDto>? Delegaciones { get; set; }
        public Guid DelegacionId { get; set; }
        public Guid ClaveId { get; set; }
        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request); Tokens.Add((request, cancellationToken));
            if (request is RevocarClaveApiCommand && EsperaRevocacion is not null) await EsperaRevocacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (request is GenerarClaveApiCommand && EsperasGeneracion.TryDequeue(out var esperaGeneracion)) await esperaGeneracion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            object respuesta = request switch
            {
                ObtenerDelegacionesQuery => Delegaciones ?? [Delegacion(DelegacionId)],
                ObtenerClavesApiQuery => new[] { Clave(ClaveId) },
                GenerarClaveApiCommand => Result.Exito(new ClaveApiGeneradaDto(ClaveId, ClavesGeneradas.TryDequeue(out var claveGenerada) ? claveGenerada : string.Concat("tlv_", new string('x', 32)), "tlv_xxxxxx")),
                RevocarClaveApiCommand => Result.Exito(),
                _ => throw new NotSupportedException()
            };
            return (T)respuesta;
        }
        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private static DelegacionDto Delegacion(Guid id, string nombre = "Organización Uno") => new(id, Guid.NewGuid(), "TALVEG", Guid.NewGuid(), nombre, true, true, DateTime.UtcNow, [], PropositoDelegacion.Soporte, null, null);
    private static ClaveApiDto Clave(Guid id) => new(id, "Integración ERP", "tlv_xxxxxx", DateTime.UtcNow, null, true);
    // Las delegaciones se leen en OnInitializedAsync: configurarlas después de renderizar no llega al componente.
    private (IRenderedComponent<ClavesApi> Cut, Mediador Mediador) Renderizar(Action<Mediador>? configurar = null)
    {
        var mediator = new Mediador { DelegacionId = Guid.NewGuid(), ClaveId = Guid.NewGuid() };
        configurar?.Invoke(mediator);
        Services.AddScoped<IMediator>(_ => mediator); Services.AddScoped<ToastService>();
        return (Render<ClavesApi>(), mediator);
    }
    private static Task Seleccionar(IRenderedComponent<ClavesApi> cut, Guid id) => cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = id.ToString() });

    [Fact]
    public async Task Lista_muestra_datos_del_dto_y_no_la_clave_completa()
    {
        var (cut, mediator) = Renderizar();
        await Seleccionar(cut, mediator.DelegacionId);
        var filas = cut.FindAll("tbody tr");
        filas.Should().ContainSingle("control positivo: el instrumento debe observar la lista").Which.TextContent.Should().Contain("Integración ERP");
        filas.Should().OnlyContain(f => f.TextContent.Contains("tlv_xxxxxx…"), "la lista solo expone el prefijo visible");
    }

    [Fact]
    public async Task Revocar_no_envia_comando_hasta_confirmar_y_usa_la_clave_de_la_fila()
    {
        var (cut, mediator) = Renderizar();
        await Seleccionar(cut, mediator.DelegacionId);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());
        mediator.Enviadas.OfType<RevocarClaveApiCommand>().Should().BeEmpty();
        var dialogo = cut.FindComponent<DialogoConfirmacion>();
        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        mediator.Enviadas.OfType<RevocarClaveApiCommand>().Should().ContainSingle("control positivo: debe haberse enviado un comando").Which.Should().Be(new RevocarClaveApiCommand(mediator.DelegacionId, mediator.ClaveId));
    }

    [Fact]
    public async Task Guarda_del_panel_bloquea_dos_entradas_por_el_callback_del_hijo()
    {
        var (cut, mediator) = Renderizar(); mediator.EsperaRevocacion = new TaskCompletionSource();
        Task? primera = null; Task? segunda = null;
        try
        {
            await Seleccionar(cut, mediator.DelegacionId);
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());
            var dialogo = cut.FindComponent<DialogoConfirmacion>();
            primera = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
            segunda = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
            mediator.Enviadas.OfType<RevocarClaveApiCommand>().Should().ContainSingle("control positivo: la primera entrada debe haber llegado al panel");
            await cut.InvokeAsync(() => mediator.EsperaRevocacion.SetResult());
            await primera.WaitAsync(TimeSpan.FromSeconds(10)); await segunda.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            mediator.EsperaRevocacion.TrySetResult();
            if (primera is not null) await primera.WaitAsync(TimeSpan.FromSeconds(10));
            if (segunda is not null) await segunda.WaitAsync(TimeSpan.FromSeconds(10));
            await DisposeComponentsAsync();
        }
    }

    [Fact]
    public async Task Cambiar_de_organizacion_reinicia_la_operacion_en_curso_y_no_deja_el_dialogo_anterior()
    {
        var (cut, mediator) = Renderizar(); mediator.EsperaRevocacion = new TaskCompletionSource();
        Task? confirmacion = null;
        try
        {
            await Seleccionar(cut, mediator.DelegacionId);
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());
            confirmacion = cut.InvokeAsync(() => cut.FindComponent<DialogoConfirmacion>().Instance.OnConfirmar.InvokeAsync());
            mediator.Enviadas.OfType<RevocarClaveApiCommand>().Should().ContainSingle("control positivo: la revocación está en curso");
            await Seleccionar(cut, Guid.NewGuid());
            cut.FindComponents<DialogoConfirmacion>().Should().ContainSingle().Which.Instance.Visible.Should().BeFalse("el contexto nuevo no conserva la confirmación ni su bandera");
            await cut.InvokeAsync(() => mediator.EsperaRevocacion.SetResult());
            await confirmacion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            mediator.EsperaRevocacion.TrySetResult();
            if (confirmacion is not null) await confirmacion.WaitAsync(TimeSpan.FromSeconds(10));
            await DisposeComponentsAsync();
        }
    }

    [Fact]
    public async Task Consultas_llevan_token_del_ciclo_comandos_no_y_dispose_lo_cancela()
    {
        var (cut, mediator) = Renderizar();
        await Seleccionar(cut, mediator.DelegacionId);
        var consultas = mediator.Tokens.Where(t => t.Peticion is ObtenerDelegacionesQuery or ObtenerClavesApiQuery).Select(t => t.Token).ToList();
        consultas.Should().NotBeEmpty("control positivo: se observaron consultas");
        consultas.Should().OnlyContain(t => t.CanBeCanceled);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(() => cut.FindComponent<DialogoConfirmacion>().Instance.OnConfirmar.InvokeAsync());
        mediator.Tokens.Where(t => t.Peticion is RevocarClaveApiCommand).Should().ContainSingle("control positivo: se observó el comando").Which.Token.CanBeCanceled.Should().BeFalse();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar clave").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar").ClickAsync(new MouseEventArgs());
        mediator.Tokens.Where(t => t.Peticion is GenerarClaveApiCommand).Should().ContainSingle("control positivo: se observó el comando").Which.Token.CanBeCanceled.Should().BeFalse();
        var token = consultas[0];
        await DisposeComponentsAsync();
        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_borra_los_datos_sensibles_del_estado_del_componente()
    {
        var (cut, mediator) = Renderizar();
        var claveEnClaro = string.Concat("tlv_", new string('z', 32));
        var campos = typeof(ClavesApi).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        // bUnit deja de exponer cut.Instance tras retirar el componente: se captura antes.
        var instancia = cut.Instance;
        campos.Single(c => c.Name == "_claveGenerada").SetValue(instancia, claveEnClaro);
        campos.Single(c => c.Name == "_nombreNuevaClave").SetValue(instancia, "Nombre pendiente");
        campos.Single(c => c.Name == "_claveARevocar").SetValue(instancia, Clave(mediator.ClaveId));
        campos.Single(c => c.Name == "_claveGenerada").GetValue(instancia).Should().Be(claveEnClaro, "control positivo: el campo se escribió");
        await DisposeComponentsAsync();
        campos.Single(c => c.Name == "_claveGenerada").GetValue(instancia).Should().BeNull();
        campos.Single(c => c.Name == "_nombreNuevaClave").GetValue(instancia).Should().BeNull();
        campos.Single(c => c.Name == "_claveARevocar").GetValue(instancia).Should().BeNull();
    }

    [Fact]
    public async Task Generacion_antigua_de_A_tras_B_y_A_no_muestra_la_clave_ni_altera_la_operacion_nueva()
    {
        var organizacionA = Guid.NewGuid(); var organizacionB = Guid.NewGuid();
        var (cut, mediator) = Renderizar(m => m.Delegaciones = [Delegacion(organizacionA, "Organización A"), Delegacion(organizacionB, "Organización B")]);
        var esperaAntigua = new TaskCompletionSource(); var esperaNueva = new TaskCompletionSource();
        var claveAntigua = string.Concat("tlv_", new string('a', 32));
        mediator.EsperasGeneracion.Enqueue(esperaAntigua); mediator.EsperasGeneracion.Enqueue(esperaNueva);
        mediator.ClavesGeneradas.Enqueue(claveAntigua); mediator.ClavesGeneradas.Enqueue(string.Concat("tlv_", new string('b', 32)));
        Task? primera = null; Task? segunda = null;
        try
        {
            await Seleccionar(cut, organizacionA);
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar clave").ClickAsync(new MouseEventArgs());
            primera = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar").ClickAsync(new MouseEventArgs());
            mediator.Enviadas.OfType<GenerarClaveApiCommand>().Should().ContainSingle("control positivo: la generación antigua está en curso");
            await Seleccionar(cut, organizacionB); await Seleccionar(cut, organizacionA);
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar clave").ClickAsync(new MouseEventArgs());
            segunda = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar").ClickAsync(new MouseEventArgs());
            mediator.Enviadas.OfType<GenerarClaveApiCommand>().Should().HaveCount(2, "control positivo: la generación nueva está en curso");
            await cut.InvokeAsync(() => esperaAntigua.SetResult()); await primera.WaitAsync(TimeSpan.FromSeconds(10));
            cut.Markup.Should().NotContain(claveAntigua, "la clave antigua no se revela tras volver a A");
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar").HasAttribute("disabled").Should().BeTrue("la generación nueva sigue en curso");
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Advertencia && m.Mensaje == "Se generó una clave para «Organización A», pero no se mostrará porque cambiaste de organización. Revócala y genera otra.");
            await cut.InvokeAsync(() => esperaNueva.SetResult()); await segunda.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            esperaAntigua.TrySetResult(); esperaNueva.TrySetResult();
            if (primera is not null) await primera.WaitAsync(TimeSpan.FromSeconds(10));
            if (segunda is not null) await segunda.WaitAsync(TimeSpan.FromSeconds(10));
            await DisposeComponentsAsync();
        }
    }

    [Fact]
    public async Task Revocacion_antigua_de_A_tras_B_y_A_no_cierra_ni_altera_la_operacion_nueva()
    {
        var organizacionA = Guid.NewGuid(); var organizacionB = Guid.NewGuid();
        var (cut, mediator) = Renderizar(m => m.Delegaciones = [Delegacion(organizacionA, "Organización A"), Delegacion(organizacionB, "Organización B")]);
        var esperaAntigua = new TaskCompletionSource(); var esperaNueva = new TaskCompletionSource();
        Task? primera = null; Task? segunda = null;
        try
        {
            mediator.EsperaRevocacion = esperaAntigua;
            await Seleccionar(cut, organizacionA);
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());
            primera = cut.InvokeAsync(() => cut.FindComponent<DialogoConfirmacion>().Instance.OnConfirmar.InvokeAsync());
            mediator.Enviadas.OfType<RevocarClaveApiCommand>().Should().ContainSingle("control positivo: la revocación antigua está en curso");
            await Seleccionar(cut, organizacionB); await Seleccionar(cut, organizacionA);
            mediator.EsperaRevocacion = esperaNueva;
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());
            segunda = cut.InvokeAsync(() => cut.FindComponent<DialogoConfirmacion>().Instance.OnConfirmar.InvokeAsync());
            mediator.Enviadas.OfType<RevocarClaveApiCommand>().Should().HaveCount(2, "control positivo: la revocación nueva está en curso");
            await cut.InvokeAsync(() => esperaAntigua.SetResult()); await primera.WaitAsync(TimeSpan.FromSeconds(10));
            cut.FindComponent<DialogoConfirmacion>().Instance.Visible.Should().BeTrue("la revocación antigua no cierra el diálogo nuevo");
            cut.FindComponent<DialogoConfirmacion>().Instance.EnProgreso.Should().BeTrue("la revocación nueva sigue en curso");
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Advertencia && m.Mensaje == "Clave revocada en «Organización A», pero no se actualizó la pantalla porque cambiaste de organización.");
            await cut.InvokeAsync(() => esperaNueva.SetResult()); await segunda.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            esperaAntigua.TrySetResult(); esperaNueva.TrySetResult();
            if (primera is not null) await primera.WaitAsync(TimeSpan.FromSeconds(10));
            if (segunda is not null) await segunda.WaitAsync(TimeSpan.FromSeconds(10));
            await DisposeComponentsAsync();
        }
    }
}
