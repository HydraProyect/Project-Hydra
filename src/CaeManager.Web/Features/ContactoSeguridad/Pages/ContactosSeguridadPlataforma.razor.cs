using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.RechazarContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.ValidarContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactosSeguridadPropuestos;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.ContactoSeguridad.Pages;

public partial class ContactosSeguridadPlataforma : CaeManager.Web.Components.PaginaInteractiva
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private bool _cargando = true, _errorCarga;
    private string? _error, _errorMotivo, _errorRechazo;
    private string _motivo = "";
    private IReadOnlyList<ContactoSeguridadPropuestoDto> _propuestos = [];
    private ContactoSeguridadPropuestoDto? _rechazando;
    private Guid? _procesandoId;

    protected override async Task OnInitializedAsync() => await CargarAsync();

    private static string Cuando(DateTime propuestoEnUtc) => propuestoEnUtc.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm");

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        try
        {
            _propuestos = await Mediator.Send(new ObtenerContactosSeguridadPropuestosQuery());
        }
        catch
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task ValidarAsync(Guid contactoId)
    {
        if (_procesandoId is not null) return;
        _procesandoId = contactoId;
        _error = null;
        try
        {
            var resultado = await Mediator.Send(new ValidarContactoSeguridadCommand(contactoId));
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ContactoValidadoAviso"], TonoToast.Exito);
            await CargarAsync();
        }
        catch
        {
            _error = Textos["ErrorResolver"];
        }
        finally
        {
            _procesandoId = null;
        }
    }

    private void AbrirRechazo(ContactoSeguridadPropuestoDto propuesto)
    {
        _rechazando = propuesto;
        _motivo = "";
        _errorMotivo = _errorRechazo = null;
        _error = null;
    }

    private void CerrarRechazo(bool visible)
    {
        if (visible || _procesandoId is not null) return;
        _rechazando = null;
    }

    private async Task RechazarAsync()
    {
        if (_rechazando is not { } propuesto || _procesandoId is not null) return;
        // El motivo es obligatorio: se comprueba aquí para no cerrar el modal, y el validador del comando
        // y la función de resolución lo vuelven a exigir.
        if (string.IsNullOrWhiteSpace(_motivo))
        {
            _errorMotivo = Textos["MotivoObligatorio"];
            return;
        }

        _procesandoId = propuesto.Id;
        _errorMotivo = _errorRechazo = null;
        try
        {
            var resultado = await Mediator.Send(new RechazarContactoSeguridadCommand(propuesto.Id, _motivo));
            if (resultado.EsFallido)
            {
                _errorRechazo = resultado.Error.Mensaje;
                return;
            }

            _rechazando = null;
            ToastService.Mostrar(Textos["ContactoRechazadoAviso"], TonoToast.Exito);
            await CargarAsync();
        }
        catch
        {
            _errorRechazo = Textos["ErrorResolver"];
        }
        finally
        {
            _procesandoId = null;
        }
    }
}
