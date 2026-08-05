using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Components;

public partial class FormularioRapidoCliente : ComponentBase
{
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private bool _guardando;
    private string? _mensajeError;
    private Dictionary<string, string> _erroresCampo = new();

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    /// <summary>Prellena la Razón social con lo que el usuario ya había escrito en el selector que disparó la creación.</summary>
    [Parameter] public string? NombreInicial { get; set; }

    [Parameter] public EventCallback<ClienteCreadoDto> OnCreado { get; set; }

    protected override void OnParametersSet()
    {
        if (!Visible) return;

        // Solo al abrir (no en cada render mientras está visible): si el
        // usuario borra el campo tras abrir el modal, no queremos que vuelva
        // a rellenarse solo.
        if (_razonSocial == string.Empty && !string.IsNullOrWhiteSpace(NombreInicial))
            _razonSocial = NombreInicial;
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
            var resultado = await Mediator.Send(new CrearClienteCommand(_razonSocial, _cif, EsCritico: false, Notas: null));
            if (resultado.EsFallido)
            {
                _mensajeError = resultado.Error.Mensaje;
                return;
            }

            var creado = new ClienteCreadoDto(resultado.Valor, _razonSocial);
            await CerrarAsync(false);
            await OnCreado.InvokeAsync(creado);
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

/// <summary>Lo mínimo que un SelectorEntidad necesita tras crear inline: el Id para seleccionarlo y el texto para mostrarlo.</summary>
public record ClienteCreadoDto(Guid Id, string RazonSocial);
