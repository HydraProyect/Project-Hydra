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

    /// <summary>Hay algo distinto de lo guardado: la clave elegida no es la de la cuenta.</summary>
    private bool HayCambios => ClaveElegida != _guardada;

    /// <summary>La línea bajo «Guardar avatar»: «Sin cambios», «Cambios sin guardar» o «Guardando…».</summary>
    private string TextoDeEstado =>
        _guardando ? Textos["AvatarGuardando"]
        : HayCambios ? Textos["AvatarCambiosSinGuardar"]
        : Textos["AvatarSinCambios"];

    /// <summary>
    /// Bajo el nombre de la vista previa: «Vista previa» si hay un animal elegido; si no, que se ve con las iniciales
    /// (con el aviso entero al llegar sin ningún avatar: es el estado vacío de la pantalla).
    /// </summary>
    private string TextoDeVistaPrevia =>
        ClaveElegida is not null ? Textos["AvatarVistaPreviaEtiqueta"]
        : _guardada is null && !_recienGuardado ? Textos["AvatarSinAvatarElegido"]
        : Textos["AvatarConTusIniciales"];

    /// <summary>«Volver a mis iniciales» no tiene nada que quitar si no hay avatar guardado ni animal elegido.</summary>
    private bool SinNadaQueQuitar => _guardada is null && _motivo is null;

    /// <summary>La vista previa «en el historial» nombra a la persona por su nombre de pila.</summary>
    private string? PrimerNombre => _nombre?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? _nombre;

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
                AvisarErrorAlGuardar(resultado.Error.Mensaje, clave);
                return;
            }

            Aplicar(CatalogoAvatares.Resolver(clave));
            _recienGuardado = true;
            Toasts.Mostrar(Textos[clave is null ? "AvatarQuitado" : "AvatarGuardado"], TonoToast.Exito);

            // El menú de cuenta de la cabecera es SSR estático: solo vuelve a leer el
            // avatar cuando el servidor pinta la página otra vez.
            Navegacion.Refresh();
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            Logger.LogError(excepcion, "No se pudo guardar el avatar de la cuenta");
            AvisarErrorAlGuardar(Textos["AvatarErrorGuardar"], clave);
        }
        finally
        {
            _guardando = false;
        }
    }

    /// <summary>Toast de error con «Reintentar»: repite el mismo guardado, sin perder lo elegido.</summary>
    private void AvisarErrorAlGuardar(string mensaje, string? clave) =>
        Toasts.Mostrar(mensaje, TonoToast.Error, Textos["AvatarReintentar"], async () =>
        {
            await GuardarAsync(clave);
            await InvokeAsync(StateHasChanged);
        });
}
