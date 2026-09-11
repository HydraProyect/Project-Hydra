using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Pages;

/// <summary>
/// Selector del Cliente empresarial cuya lectura IA se va a restringir
/// (Nivel 2). La configuración en sí vive en
/// <c>/clientes/{id}/lectura-ia</c> (<c>ConfiguracionIaCliente</c>); esta
/// pantalla solo elige a quién, y cada fila es un enlace que navega de verdad.
///
/// <para>
/// <b>Lo que el Nivel 2 hace hoy, medido en el código y no en el mockup</b>
/// (por eso la entradilla no dice «qué se extrae»): el Nivel 1
/// (<c>TipoDocumento.LecturaIaActiva</c>) lo consultan la verificación y la
/// detección de trabajadores; el Nivel 2 (<c>ConfiguracionIaDocumentoCliente</c>)
/// solo lo consulta <c>DeteccionTrabajadoresService</c>, que omite un documento
/// de empresa únicamente cuando todos los Clientes empresariales a los que esa
/// empresa presta servicio lo tienen desactivado. <c>VerificacionIaDocumentoService</c>
/// no mira el Nivel 2 (lo dice su propio comentario). Si eso cambia, la nota
/// de alcance de la página tiene que cambiar con ello.
/// </para>
///
/// <para>
/// La pantalla es solo de lectura: no hay escrituras, ni proveedor de IA que
/// elegir (lo fija <c>DocumentAIProviderFactory</c> para todo el despliegue),
/// ni claves que introducir — las del proveedor son de TALVEG.
/// </para>
///
/// <para>
/// Con el gate de rol actual (Administrador) el alcance de
/// <see cref="ObtenerClientesParaSelectorQuery"/> es toda la organización, así
/// que el estado vacío no habla de «tu cartera»: habla de que no hay ninguno.
/// </para>
/// </summary>
public partial class SeleccionarClienteLecturaIa : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ILogger<SeleccionarClienteLecturaIa> Logger { get; set; } = default!;

    private bool _cargando = true;
    private bool _error;
    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private string _filtro = string.Empty;

    /// <summary>
    /// Número de la carga vigente. Cada carga lo captura antes del
    /// <c>await</c> y descarta su respuesta si ya no coincide: otra carga
    /// posterior o la retirada del componente la han dejado obsoleta.
    /// </summary>
    private int _versionCarga;

    /// <summary>
    /// Filtro EN MEMORIA sobre la lista ya cargada (nota del mockup): sin ida
    /// al servidor. <see cref="Components.DesignSystem.CampoTexto"/> ya aplica
    /// su debounce antes de notificar.
    /// </summary>
    private IEnumerable<ClienteSelectorDto> ClientesFiltrados
    {
        get
        {
            var filtro = _filtro.Trim();
            return filtro.Length == 0
                ? _clientes
                : _clientes.Where(c => c.RazonSocial.Contains(filtro, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool SinCoincidencias => _filtro.Trim().Length > 0 && !ClientesFiltrados.Any();

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>
    /// <see cref="Components.DesignSystem.Boton"/> deja el <c>@onclick</c>
    /// enganchado aunque esté en carga: la guarda de doble clic no puede
    /// vivir solo en el atributo <c>disabled</c>.
    /// </summary>
    private Task ReintentarAsync() => _cargando ? Task.CompletedTask : CargarAsync();

    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        _cargando = true;
        StateHasChanged();

        try
        {
            var clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
            if (version != _versionCarga) return;

            _clientes = clientes;
            _error = false;
        }
        catch (Exception ex)
        {
            if (version != _versionCarga) return;

            Logger.LogError(ex, "Error al cargar los Clientes empresariales para la lectura IA.");
            _error = true;
        }

        _cargando = false;
    }

    public void Dispose() => _versionCarga++;
}
