using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Guarda la preferencia de tema en la cuenta del usuario (ApplicationUser.Tema),
/// no en localStorage — así viaja con la cuenta entre dispositivos, igual que
/// cualquier otro ajuste de usuario. Se aplica sobre &lt;html data-theme&gt; vía
/// JS interop; como esto ocurre después de que el circuito de Blazor conecta
/// (no en el HTML servido inicialmente), un usuario con Claro u Oscuro
/// explícito puede ver un parpadeo muy breve al tema del sistema en la
/// primera carga — se acepta ese coste a cambio de no añadir el mecanismo de
/// leer la cookie de sesión desde el HTML servido en frío, que ningún otro
/// componente de la app necesita todavía.
/// </summary>
public partial class SelectorTema : ComponentBase, IAsyncDisposable
{
    private IJSObjectReference? _modulo;
    private ApplicationUser? _usuario;
    private string? _temaActual;

    protected override async Task OnInitializedAsync()
    {
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(idClaim, out var usuarioId)) return;

        _usuario = await UserManager.FindByIdAsync(usuarioId.ToString());
        _temaActual = TemaATexto(_usuario?.Tema ?? TemaPreferido.Sistema);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _temaActual is null) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/tema.js");
        await _modulo.InvokeVoidAsync("aplicarTema", _temaActual);
    }

    private async Task CambiarTemaAsync(ChangeEventArgs e)
    {
        var texto = e.Value?.ToString() ?? "sistema";
        _temaActual = texto;

        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("aplicarTema", texto);

        if (_usuario is not null)
        {
            _usuario.Tema = TextoATema(texto);
            await UserManager.UpdateAsync(_usuario);
        }
    }

    private static string TemaATexto(TemaPreferido tema) => tema switch
    {
        TemaPreferido.Claro => "claro",
        TemaPreferido.Oscuro => "oscuro",
        _ => "sistema"
    };

    private static TemaPreferido TextoATema(string texto) => texto switch
    {
        "claro" => TemaPreferido.Claro,
        "oscuro" => TemaPreferido.Oscuro,
        _ => TemaPreferido.Sistema
    };

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not null)
            await _modulo.DisposeAsync();
    }
}
