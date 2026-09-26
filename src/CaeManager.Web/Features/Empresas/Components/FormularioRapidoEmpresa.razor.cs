using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Components;

public partial class FormularioRapidoEmpresa : ComponentBase
{
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private bool _guardando;
    private string? _mensajeError;
    private Dictionary<string, string> _erroresCampo = new();

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public string? NombreInicial { get; set; }
    [Parameter] public Guid? ClienteIdParaVincular { get; set; }
    [Parameter] public EventCallback<EmpresaCreadaDto> OnCreado { get; set; }

    private readonly InstantaneaFormulario _instantanea = new();
    private bool _visibleAnterior;

    /// <summary>
    /// P1-E2b: si hay algo escrito que se perdería al cerrar este modal. Lo recibe su
    /// Modal (la X, Escape o el fondo preguntan «¿Descartar cambios?») y lo lee la pantalla
    /// que lo monta para sumarlo a su único AvisoCambiosSinGuardar: el modal no monta un
    /// aviso propio, que con cambios también debajo haría dos preguntas seguidas al salir.
    /// El nombre traído del selector que disparó la creación no es un cambio.
    /// </summary>
    public bool HayCambiosSinGuardar => Visible && _instantanea.Difiere(ValoresFormulario());

    protected override void OnParametersSet()
    {
        // Solo al abrir (no en cada render mientras está visible): si el usuario
        // borra el campo tras abrir el modal, no queremos que vuelva a rellenarse
        // solo. Al abrir se parte de cero aunque quien lo monta lo cerrara sin pasar
        // por CerrarAsync (salir descartando desde el aviso de la pantalla).
        if (Visible && !_visibleAnterior)
        {
            _razonSocial = string.IsNullOrWhiteSpace(NombreInicial) ? string.Empty : NombreInicial;
            _cif = string.Empty;
            _mensajeError = null;
            _erroresCampo = new Dictionary<string, string>();
            _instantanea.Fijar(ValoresFormulario());
        }

        _visibleAnterior = Visible;
    }

    private object?[] ValoresFormulario() => [_razonSocial, _cif];

    /// <summary>
    /// P1-E2b: se invoca con cada tecla. Teclear aquí solo repinta este modal, y la pantalla
    /// que suma <see cref="HayCambiosSinGuardar"/> a su aviso necesita repintarse también:
    /// el aviso del navegador al recargar o cerrar la pestaña (ConfirmExternalNavigation)
    /// se decide en el render, no al navegar.
    /// </summary>
    [Parameter] public EventCallback OnCambio { get; set; }

    private Task CambiarRazonSocialAsync(string valor)
    {
        _razonSocial = valor;
        return OnCambio.InvokeAsync();
    }

    private Task CambiarCifAsync(string valor)
    {
        _cif = valor;
        return OnCambio.InvokeAsync();
    }

    private Task CerrarAsync(bool visible)
    {
        if (!visible)
        {
            _razonSocial = string.Empty;
            _cif = string.Empty;
            _mensajeError = null;
            _erroresCampo = new Dictionary<string, string>();
        }

        return VisibleChanged.InvokeAsync(visible);
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeError = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;
            var clienteIds = ClienteIdParaVincular is null
                ? Array.Empty<Guid>()
                : [ClienteIdParaVincular.Value];

            var resultado = await Mediator.Send(new CrearEmpresaCommand(_razonSocial, cif, clienteIds));
            if (resultado.EsFallido)
            {
                _mensajeError = resultado.Error.Mensaje;
                return;
            }

            var creada = new EmpresaCreadaDto(resultado.Valor, _razonSocial);
            await CerrarAsync(false);
            await OnCreado.InvokeAsync(creada);
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeError = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);
}

public record EmpresaCreadaDto(Guid Id, string RazonSocial);
