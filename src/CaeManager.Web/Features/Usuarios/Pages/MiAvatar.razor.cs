using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.ElegirAvatarPropio;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Usuarios.Pages;

/// <summary>
/// Donde cada persona elige su avatar entre los de <see cref="CatalogoAvatares"/> o vuelve
/// a sus iniciales. Se llega desde el menú de cuenta, pulsando el propio avatar. Es de la
/// cuenta, no del Tenant: no depende de la organización activa ni del rol.
/// </summary>
public partial class MiAvatar : CaeManager.Web.Components.PaginaInteractiva
{
    /// <summary>Motivo con el que se enseñan los tonos mientras no se ha elegido ninguno.</summary>
    private static readonly string MotivoDeMuestra = CatalogoAvatares.Motivos[0].Clave;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _guardando;
    private string? _nombre;

    /// <summary>Clave guardada en la cuenta, ya normalizada: <c>null</c> si no es del catálogo.</summary>
    private string? _guardada;

    /// <summary>
    /// Enciende el rebote de la vista previa al guardar. Se apaga al empezar el guardado
    /// siguiente y al cambiar la elección: la animación solo arranca cuando la clase entra.
    /// </summary>
    private bool _recienGuardado;

    private string? _motivo;
    private string _tono = CatalogoAvatares.Tonos[0];

    /// <summary>Lo que se guardaría ahora: <c>null</c> mientras no haya animal elegido.</summary>
    private string? ClaveElegida => _motivo is null ? null : CatalogoAvatares.Clave(_motivo, _tono);

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            if (EstadoAutenticacion is not null)
                _nombre = (await EstadoAutenticacion).User.Identity?.Name;

            Aplicar(CatalogoAvatares.Resolver(await Mediator.Send(new ObtenerAvatarPropioQuery())));
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            Logger.LogError(excepcion, "No se pudo cargar el avatar de la cuenta");
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private void Aplicar(AvatarElegido? elegido)
    {
        _guardada = elegido?.Clave;
        _motivo = elegido?.Motivo.Clave;
        if (elegido is not null) _tono = elegido.Tono;
    }

    private void ElegirMotivo(string motivo)
    {
        _motivo = motivo;
        _recienGuardado = false;
    }

    private void ElegirTono(string tono)
    {
        _tono = tono;
        _recienGuardado = false;
    }

    private async Task GuardarAsync(string? clave)
    {
        if (_guardando) return;
        _guardando = true;
        _recienGuardado = false;

        try
        {
            var resultado = await Mediator.Send(new ElegirAvatarPropioCommand(clave));
            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            Aplicar(CatalogoAvatares.Resolver(clave));
            _recienGuardado = true;
            Toasts.Mostrar(Textos[clave is null ? "AvatarQuitado" : "AvatarGuardado"], TonoToast.Exito);

            // El menú de cuenta de la cabecera es SSR estático: solo vuelve a leer el
            // avatar cuando el servidor pinta la página otra vez.
            Navegacion.Refresh();
        }
        finally
        {
            _guardando = false;
        }
    }
}
