using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Empresas.Components;

/// <summary>
/// «Preparar paquete» de Empresa 360: botón de la cabecera y su diálogo. Al abrir elige el primer Cliente empresarial (como la
/// maqueta) o, sin ninguno, «Todo lo vigente». Cada cambio de destino vuelve a pedir el resumen sin retirar los botones de
/// destino, para que el foco del teclado se quede donde estaba, y el resumen nuevo se anuncia (<c>aria-live</c>). «Generar
/// ZIP» descarga por fetch (<c>wwwroot/js/descarga-archivo.js</c>) del endpoint autenticado con el destino del resumen
/// anunciado —fijado antes de cualquier espera, y con los destinos deshabilitados mientras se genera—, que vuelve a
/// autorizar y a seleccionar: el resumen es solo lo que se anuncia, y si el servidor rechaza la descarga el motivo se ve en
/// el diálogo.
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
    private int _versionCarga;
    private bool _generando;
    private string? _errorDescarga;
    private IJSObjectReference? _modulo;

    /// <summary>
    /// La ruta del zip para el destino con el que se calculó el resumen que se está anunciando
    /// (<see cref="ResumenPaqueteEmpresaDto.ClienteEmpresarialId"/>), no para el botón pulsado en este momento: lo que se
    /// descarga es exactamente lo que el diálogo dijo que iba.
    /// </summary>
    internal string RutaDescargaDe(ResumenPaqueteEmpresaDto resumen) => resumen.ClienteEmpresarialId is { } id
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
        // Mientras se genera el zip el destino no cambia (los botones están deshabilitados): el resumen anunciado tiene que
        // seguir siendo el del paquete que se está descargando.
        if (_generando) return;
        _destinoId = destinoId;
        _errorDescarga = null;
        await CargarAsync();
    }

    /// <summary>
    /// Cada carga lleva su versión: si el Gestor CAE cambia de destino antes de que llegue la respuesta anterior, esa
    /// respuesta se descarta, para que el diálogo nunca anuncie el resumen de un destino con otro pulsado. Mientras carga,
    /// «Generar ZIP» está deshabilitado.
    /// </summary>
    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        _cargando = true;
        _error = false;
        _mensajeError = null;
        try
        {
            var resultado = await Mediator.Send(new ObtenerResumenPaqueteEmpresaQuery(EmpresaId, _destinoId));
            if (version != _versionCarga) return;
            if (resultado.EsFallido)
            {
                _error = true;
                _mensajeError = resultado.Error.Mensaje;
                _resumen = null;
                return;
            }

            // La maqueta abre con el primer Cliente empresarial elegido: es lo que se le pide al Gestor CAE. El resumen de
            // «Todo lo vigente» de esta primera consulta no se pinta: solo da los destinos.
            if (!_destinoInicialElegido)
            {
                _destinoInicialElegido = true;
                if (resultado.Valor.Destinos.Count > 0)
                {
                    _destinoId = resultado.Valor.Destinos[0].ClienteEmpresarialId;
                    await CargarAsync();
                    return;
                }
            }

            _resumen = resultado.Valor;
        }
        catch (Exception) when (!_abierto || version != _versionCarga)
        {
            // Cerrado mientras cargaba, o ya superada por otra carga: no hay nada que pintar.
        }
        catch (Exception)
        {
            _error = true;
            _resumen = null;
        }
        finally
        {
            if (version == _versionCarga)
                _cargando = false;
        }
    }

    private async Task GenerarAsync()
    {
        if (_generando || _cargando || !OfreceGenerar || _resumen is not { } resumen) return;

        // La ruta se fija antes de cualquier espera y sale del resumen anunciado: nada de lo que pase mientras se importa el
        // módulo o se descarga cambia qué paquete se pide.
        var ruta = RutaDescargaDe(resumen);
        _generando = true;
        _errorDescarga = null;
        try
        {
            _modulo ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/descarga-archivo.js");
            var respuesta = await _modulo.InvokeAsync<RespuestaDescarga>("descargar", ruta, "paquete-documental.zip");
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
