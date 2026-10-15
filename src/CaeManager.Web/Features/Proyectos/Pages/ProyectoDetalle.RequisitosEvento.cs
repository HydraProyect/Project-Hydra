using CaeManager.Application.Proyectos.Commands.RequisitosEvento;
using CaeManager.Application.Proyectos.Queries.RequisitosEvento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Proyectos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Proyectos.Pages;

/// <summary>
/// Requisitos documentales de un evento (Proyecto de tipo <see cref="TipoProyecto.Evento"/>): se listan, se añaden
/// o se quitan uno a uno, y se guardan como plantilla del Cliente empresarial para copiarlos al crear el siguiente.
/// </summary>
public partial class ProyectoDetalle
{
    private IReadOnlyList<RequisitoEventoDto> _requisitosEvento = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDocumentoDisponibles = [];
    private bool _errorRequisitosEvento;
    private string _tipoDocumentoAAnadir = string.Empty;
    private string _nombrePlantilla = string.Empty;
    private bool _guardandoRequisito;

    private bool EsEvento => _detalle?.Tipo == TipoProyecto.Evento;

    private IEnumerable<TipoDocumentoListaDto> TiposDocumentoAnadibles =>
        _tiposDocumentoDisponibles.Where(t => _requisitosEvento.All(r => r.TipoDocumentoId != t.Id));

    private async Task CargarRequisitosEventoAsync()
    {
        if (!EsEvento) return;
        try
        {
            _requisitosEvento = await Mediator.Send(new ObtenerRequisitosEventoQuery(ProyectoId), _cancelacion);
            if (_tiposDocumentoDisponibles.Count == 0)
                _tiposDocumentoDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(), _cancelacion);
            _errorRequisitosEvento = false;
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _errorRequisitosEvento = true;
        }
    }

    private async Task AnadirRequisitoEventoAsync()
    {
        if (!Guid.TryParse(_tipoDocumentoAAnadir, out var tipoDocumentoId)) return;
        await EjecutarRequisitoAsync(new AnadirRequisitoEventoCommand(ProyectoId, tipoDocumentoId), Textos["ToastRequisitoAnadido"]);
        _tipoDocumentoAAnadir = string.Empty;
    }

    private Task QuitarRequisitoEventoAsync(Guid tipoDocumentoId) =>
        EjecutarRequisitoAsync(new QuitarRequisitoEventoCommand(ProyectoId, tipoDocumentoId), Textos["ToastRequisitoQuitado"]);

    private async Task EjecutarRequisitoAsync(MediatR.IRequest<Domain.Common.Result> comando, string textoExito)
    {
        _guardandoRequisito = true;
        try
        {
            var resultado = await Mediator.Send(comando);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(textoExito, TonoToast.Exito);
            await CargarRequisitosEventoAsync();
        }
        catch (Exception)
        {
            // El circuito no cae por un fallo de la base: se avisa y la lista se relee.
            ToastService.Mostrar(Textos["ErrorRequisitosEvento"], TonoToast.Error);
            await CargarRequisitosEventoAsync();
        }
        finally
        {
            _guardandoRequisito = false;
        }
    }

    private async Task GuardarPlantillaRequisitosAsync()
    {
        if (string.IsNullOrWhiteSpace(_nombrePlantilla)) return;
        _guardandoRequisito = true;
        try
        {
            var resultado = await Mediator.Send(new GuardarPlantillaRequisitosEventoCommand(ProyectoId, _nombrePlantilla));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastPlantillaGuardada"], TonoToast.Exito);
            _nombrePlantilla = string.Empty;
        }
        finally
        {
            _guardandoRequisito = false;
        }
    }
}
