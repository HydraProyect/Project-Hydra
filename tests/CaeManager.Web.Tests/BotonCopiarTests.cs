using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// clipboard.js resolvía su promesa aunque el respaldo con
/// <c>document.execCommand('copy')</c> devolviera <c>false</c> (permisos o
/// política del navegador) — BotonCopiar anunciaba «Se copió…» sin haber
/// copiado nada, y en Claves API eso significa perder la clave sin aviso,
/// porque el modal la borra al confirmar «Ya la copié». Lo que se prueba
/// aquí es el contrato en el lado de C#: un fallo del interop nunca produce
/// el toast de éxito, con control positivo del caso que sí copia. El
/// interop real del portapapeles (execCommand, Clipboard API) no se observa
/// bajo bUnit — de eso no hay evidencia aquí, solo de que BotonCopiar
/// reacciona bien a lo que el módulo le devuelva.
/// </summary>
public class BotonCopiarTests : BunitContext
{
    private const string Modulo = "./js/clipboard.js";

    public BotonCopiarTests()
    {
        Services.AddSingleton<ToastService>();
    }

    private IReadOnlyList<ToastMensaje> Toasts() => Services.GetRequiredService<ToastService>().Mensajes;

    [Fact]
    public async Task Cuando_el_interop_copia_de_verdad_anuncia_exito()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();

        var cut = Render<BotonCopiar>(p => p.Add(c => c.Valor, "12345678Z").Add(c => c.Etiqueta, "el DNI"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal("12345678Z");
        Toasts().Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje.Contains("el DNI"));
    }

    [Fact]
    public async Task Cuando_el_interop_falla_nunca_anuncia_exito()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("copiarAlPortapapeles", _ => true)
            .SetException(new JSException("execCommand(\"copy\") devolvió false."));

        var cut = Render<BotonCopiar>(p => p.Add(c => c.Valor, "12345678Z").Add(c => c.Etiqueta, "el DNI"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        Toasts().Should().ContainSingle(t => t.Tono == TonoToast.Error && t.Mensaje.Contains("el DNI"));
        Toasts().Should().NotContain(t => t.Tono == TonoToast.Exito, "un fallo del interop nunca puede colarse como éxito");
    }

    [Fact]
    public async Task El_fallo_con_Valor_visible_en_pantalla_sugiere_copiarlo_a_mano()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetException(new JSException("fallo simulado"));

        var cut = Render<BotonCopiar>(p => p.Add(c => c.Valor, "clave-generada").Add(c => c.Etiqueta, "la clave API"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        Toasts().Should().ContainSingle().Which.Mensaje.Should().Contain("Selecciónalo y cópialo a mano");
    }

    [Fact]
    public async Task El_fallo_con_ValorAsync_no_sugiere_copiar_a_mano_porque_nada_esta_visible()
    {
        // ValorAsync es el caso de DEC-53/DEC-62 (p. ej. una contraseña): el
        // valor no se precarga ni se pinta en pantalla, así que no hay nada
        // que seleccionar a mano si falla la copia.
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetException(new JSException("fallo simulado"));

        var cut = Render<BotonCopiar>(p => p
            .Add(c => c.ValorAsync, () => Task.FromResult<string?>("s3cr3t"))
            .Add(c => c.Etiqueta, "la contraseña"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        var mensaje = Toasts().Should().ContainSingle().Which.Mensaje;
        mensaje.Should().Contain("la contraseña").And.NotContain("Selecciónalo y cópialo a mano");
    }
}
