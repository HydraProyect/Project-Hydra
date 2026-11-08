using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Empresas.Components;

/// <summary>
/// «Preparar paquete» de Empresa 360: botón de la cabecera y su diálogo. Al abrir elige el primer Cliente empresarial (como la
/// maqueta) o, sin ninguno, «Todo lo vigente». Cada cambio de destino vuelve a pedir el resumen sin retirar los botones de
/// destino, para que el foco del teclado se quede donde estaba, y el resumen nuevo se anuncia (<c>aria-live</c>). «Generar
/// ZIP» descarga por fetch (<c>wwwroot/js/descarga-archivo.js</c>) del endpoint autenticado con el destino elegido, que
/// vuelve a autorizar y a seleccionar: el resumen es solo lo que se anuncia, y si el servidor rechaza la descarga el motivo
/// se ve en el diálogo.
/// </summary>
public partial class BotonPaqueteEmpresa : IAsyncDisposable
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
    private bool _generando;
    private string? _errorDescarga;
    private IJSObjectReference? _modulo;

    internal string RutaDescarga => _destinoId is { } id
        ? $"/empresas/{EmpresaId}/paquete-documental.zip?cliente={id}"
        : $"/empresas/{EmpresaId}/paquete-documental.zip";

    /// <summary>Por encima del tope síncrono no se ofrece «Generar ZIP»: el servidor lo rechazaría (409).</summary>
    private bool PasaDelLimite => _resumen is { } r && r.Incluye > IPaqueteDocumentalEmpresaService.MaximoArchivos;

    private bool OfreceGenerar => _resumen is { Incluye: > 0, PuedeDescargar: true } && !PasaDelLimite;

    private async Task AbrirAsync()
    {
        _abierto = true;
        _destinoInicialElegido = false;
        _destinoId = null;
        _resumen = null;
        _errorDescarga = null;
        await CargarAsync();
    }

    private async Task ElegirAsync(Guid? destinoId)
    {
        _destinoId = destinoId;
        _errorDescarga = null;
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

    private async Task GenerarAsync()
    {
        if (_generando || !OfreceGenerar) return;

        _generando = true;
        _errorDescarga = null;
        try
        {
            _modulo ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/descarga-archivo.js");
            var respuesta = await _modulo.InvokeAsync<RespuestaDescarga>("descargar", RutaDescarga, "paquete-documental.zip");
            if (!respuesta.Ok)
                _errorDescarga = MensajeDeDescarga(respuesta);
        }
        catch (JSDisconnectedException)
        {
            // El circuito se fue: no queda pantalla en la que avisar.
        }
        catch (Exception)
        {
            _errorDescarga = Textos["ErrorDescarga"];
        }
        finally
        {
            _generando = false;
        }
    }

    /// <summary>
    /// 403 y 409 traen el motivo en texto (rol, Sesión Privilegiada, tope, nada vigente, archivos que no se abren); un 404 es
    /// que la Empresa o el destino ya no están a tu alcance; lo demás, el genérico.
    /// </summary>
    private string MensajeDeDescarga(RespuestaDescarga respuesta) => respuesta.Estado switch
    {
        403 or 409 when !string.IsNullOrWhiteSpace(respuesta.Mensaje) => respuesta.Mensaje!,
        404 => Textos["ErrorDescargaNoEncontrada"],
        _ => Textos["ErrorDescarga"],
    };

    /// <summary>Lo que devuelve <c>descargar</c> de <c>descarga-archivo.js</c>.</summary>
    public sealed record RespuestaDescarga(bool Ok, int Estado, string? Mensaje);

    private void IrAReclamar()
    {
        _abierto = false;
        NavigationManager.NavigateTo($"/empresas/{EmpresaId}?pestana=reclamar");
    }

    public async ValueTask DisposeAsync()
    {
        if (_modulo is null) return;
        try
        {
            await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // El circuito ya no existe: no hay módulo que liberar.
        }
    }
}
