using CaeManager.Application.Empresas.Commands.CrearEmpresa;
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

    protected override void OnParametersSet()
    {
        if (!Visible) return;

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
