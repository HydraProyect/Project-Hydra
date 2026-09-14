using System.Globalization;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

public class TextoFechaCopiableTests : BunitContext
{
    public TextoFechaCopiableTests() => Services.AddSingleton<ToastService>();

    [Theory]
    [InlineData(false, true, "02/08/2026")]
    [InlineData(true, true, "15/08/2025")]
    [InlineData(true, false, "02/08/2026")]
    public async Task El_gesto_elige_la_fecha_completa_con_formato_invariable(bool alt, bool tieneEmision, string esperado)
    {
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var culturaAnterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var cut = Render<TextoFechaCopiable>(p => p
                .Add(c => c.Fecha, new DateOnly(2026, 8, 2))
                .Add(c => c.FechaEmision, tieneEmision ? new DateOnly(2025, 8, 15) : null)
                .AddChildContent("Venció 02/08"));
            await cut.Find("button").ClickAsync(new MouseEventArgs { AltKey = alt });

            modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal(esperado);
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito);
            cut.FindAll("input").Should().BeEmpty();
            cut.Find("button").GetAttribute("title").Should().Contain("02/08/2026");
            if (tieneEmision) cut.Find("button").GetAttribute("aria-label").Should().Contain("Alt/Option + clic").And.Contain("15/08/2025");
        }
        finally { CultureInfo.CurrentCulture = culturaAnterior; }
    }

    [Fact]
    public void Sin_vencimiento_no_ofrece_un_gesto_de_copia()
    {
        var cut = Render<TextoFechaCopiable>(p => p.Add(c => c.FechaEmision, new DateOnly(2025, 8, 15)).AddChildContent("Sin caducidad"));
        cut.FindAll("button").Should().BeEmpty();
        cut.Markup.Should().Contain("Sin caducidad");
        JSInterop.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "02/08/2026")]
    [InlineData(true, "15/08/2025")]
    public async Task El_fallo_ofrece_la_fecha_elegida_para_copiar_manualmente_sin_anunciar_exito(bool alt, string esperado)
    {
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetException(new JSException("Copia denegada"));
        modulo.SetupVoid("seleccionarFechaParaCopiaManual", _ => true).SetVoidResult();
        var cut = Render<TextoFechaCopiable>(p => p.Add(c => c.Fecha, new DateOnly(2026, 8, 2))
            .Add(c => c.FechaEmision, new DateOnly(2025, 8, 15)).AddChildContent("Venció 02/08"));

        await cut.Find("button").ClickAsync(new MouseEventArgs { AltKey = alt });

        cut.Find("input[readonly]").GetAttribute("value").Should().Be(esperado);
        cut.Find("[role=status]").TextContent.Should().Contain("Ctrl/Cmd + C");
        modulo.VerifyInvoke("seleccionarFechaParaCopiaManual");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();

        cut.Render(p => p.Add(c => c.Fecha, new DateOnly(2027, 8, 2)));
        cut.FindAll("input").Should().BeEmpty("la recuperación anterior no debe mostrar una fecha de otro render");
    }

    [Fact]
    public async Task Si_no_se_puede_cargar_el_modulo_la_fecha_sigue_disponible_para_copia_manual()
    {
        Services.AddSingleton<IJSRuntime>(new JsRuntimeNoDisponible());
        var cut = Render<TextoFechaCopiable>(p => p.Add(c => c.Fecha, new DateOnly(2026, 8, 2)));
        await cut.Find("button").ClickAsync(new MouseEventArgs());
        cut.Find("input").GetAttribute("value").Should().Be("02/08/2026");
    }

    private sealed class JsRuntimeNoDisponible : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("No se pudo cargar el módulo"));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
