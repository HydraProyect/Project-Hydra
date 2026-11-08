using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Components;

/// <summary>
/// «Preparar paquete» de Empresa 360: botón de la cabecera y su diálogo. Al abrir elige el primer Cliente empresarial (como la
/// maqueta) o, sin ninguno, «Todo lo vigente». Cada cambio de destino vuelve a pedir el resumen; la descarga es un enlace al
/// endpoint autenticado con el destino elegido, que vuelve a autorizar y a seleccionar: el resumen es solo lo que se anuncia.
/// </summary>
public partial class BotonPaqueteEmpresa
{
    [Parameter, EditorRequired] public Guid EmpresaId { get; set; }
    [Parameter] public string? RazonSocial { get; set; }

    private string IdPara => $"paquete-para-{EmpresaId:N}";
    private bool _abierto;
    private bool _cargando;
    private bool _error;
    private string? _mensajeError;
    private bool _destinoInicialElegido;
    private Guid? _destinoId;
    private ResumenPaqueteEmpresaDto? _resumen;

    internal string RutaDescarga => _destinoId is { } id
        ? $"/empresas/{EmpresaId}/paquete-documental.zip?cliente={id}"
        : $"/empresas/{EmpresaId}/paquete-documental.zip";

    private async Task AbrirAsync()
    {
        _abierto = true;
        _destinoInicialElegido = false;
        _destinoId = null;
        await CargarAsync();
    }

    private async Task ElegirAsync(Guid? destinoId)
    {
        _destinoId = destinoId;
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        _mensajeError = null;
        try
        {
            var resultado = await Mediator.Send(new ObtenerResumenPaqueteEmpresaQuery(EmpresaId, _destinoId));
            if (resultado.EsFallido)
            {
                _error = true;
                _mensajeError = resultado.Error.Mensaje;
                _resumen = null;
                return;
            }

            _resumen = resultado.Valor;

            // La maqueta abre con el primer Cliente empresarial elegido: es lo que se le pide al Gestor CAE.
            if (!_destinoInicialElegido)
            {
                _destinoInicialElegido = true;
                if (_resumen.Destinos.Count > 0)
                {
                    _destinoId = _resumen.Destinos[0].ClienteEmpresarialId;
                    await CargarAsync();
                }
            }
        }
        catch (Exception) when (!_abierto)
        {
            // Cerrado mientras cargaba: no hay nada que pintar.
        }
        catch (Exception)
        {
            _error = true;
            _resumen = null;
        }
        finally
        {
            _cargando = false;
        }
    }

    private void IrAReclamar()
    {
        _abierto = false;
        NavigationManager.NavigateTo($"/empresas/{EmpresaId}?pestana=reclamar");
    }
}
