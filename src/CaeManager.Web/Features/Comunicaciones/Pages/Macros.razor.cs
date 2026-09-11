using System.Net;
using System.Text.RegularExpressions;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.CrearMacro;
using CaeManager.Application.Comunicaciones.Commands.EditarMacro;
using CaeManager.Application.Comunicaciones.Commands.EliminarMacro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

public partial class Macros : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    /// <summary>Largo máximo del resumen del contenido bajo el título de cada fila.</summary>
    internal const int LongitudResumen = 96;

    [Inject] private ILogger<Macros> Logger { get; set; } = default!;
    [Inject] private IOptions<ComunicacionesOptions> OpcionesComunicaciones { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private string _clienteFiltro = string.Empty;
    // El filtro con el que se pidió la lista que hay en pantalla. El resumen y
    // el recuento hablan de ESTA lista, no del valor que tenga ahora el
    // selector mientras la siguiente carga sigue en vuelo.
    private string _clienteFiltroCargado = string.Empty;
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<MacroListaDto> _macros = [];

    private bool _cargando = true;
    private bool _errorCarga;

    // Número de la carga vigente. Cada carga lo incrementa antes del await y,
    // al volver, se descarta si ya no es la última: sin esto, cambiar dos
    // veces de cliente seguido podía pintar las macros del primero bajo el
    // segundo si su respuesta llegaba después.
    private int _solicitudMacros;

    // Paginación en memoria: ObtenerMacrosQuery devuelve la lista entera.
    private int _paginaActual = 1;
    private int _tamanoPagina = 20;

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
    // Cliente que tenía la macro al abrirla: con él se vuelve a pedir la
    // versión actual tras un conflicto.
    private Guid? _clienteIdOriginal;
    private string _titulo = string.Empty;
    private string _cuerpo = string.Empty;
    private string _clienteIdFormulario = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    // Conflicto de concurrencia optimista al guardar una edición.
    private bool _conflicto;
    private bool _recargandoVersion;
    private int _solicitudVersion;

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _tituloAEliminar = string.Empty;
    private bool _eliminando;

    protected override async Task OnInitializedAsync()
    {
        // Módulo congelado por defecto (ComunicacionesOptions, P2 #26 de
        // docs/business/MATURITY_REVIEW.md): sin ingesta real de Graph
        // detrás, se presenta como si la ruta no existiera en vez de
        // mostrar una bandeja que nadie va a alimentar de verdad.
        if (!OpcionesComunicaciones.Value.Activo)
        {
            NavigationManager.NavigateTo("/not-found");
            return;
        }

        _clientesSelector = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        var solicitud = ++_solicitudMacros;
        var filtro = _clienteFiltro;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var clienteId = Guid.TryParse(filtro, out var id) ? id : (Guid?)null;
            var macros = await Mediator.Send(new ObtenerMacrosQuery(clienteId));
            if (solicitud != _solicitudMacros) return;

            _macros = macros;
            _clienteFiltroCargado = filtro;
            _paginaActual = Math.Clamp(_paginaActual, 1, TotalPaginas);
        }
        catch (Exception ex)
        {
            if (solicitud != _solicitudMacros) return;

            Logger.LogError(ex, "Error al cargar las macros de respuesta.");
            _errorCarga = true;
        }
        finally
        {
            if (solicitud == _solicitudMacros)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private Task FiltrarPorClienteAsync(string clienteId)
    {
        _clienteFiltro = clienteId;
        _paginaActual = 1;
        return CargarAsync();
    }

    /// <summary>
    /// El único filtro de la pantalla, y el único de todo el defecto sistémico
    /// del vacío por filtro que <b>ensancha</b> el resultado en vez de
    /// estrecharlo: sin cliente, <c>ObtenerMacrosQuery</c> devuelve solo las
    /// macros genéricas; con un cliente, las genéricas MÁS las suyas.
    ///
    /// <para>
    /// Por eso esta pantalla no tiene <c>LimpiarFiltrosAsync</c> como las
    /// otras ocho: "quitar el filtro" sobre un resultado vacío enseñaría
    /// menos, no más — el sin-filtro es un subconjunto del con-filtro. Es la
    /// misma clase de trampa que Visitas, donde copiar el patrón mecánico
    /// habría empeorado la pantalla.
    /// </para>
    /// </summary>
    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_clienteFiltro);

    // ------------------------------------------------------------ recuentos

    private int TotalGenericas => _macros.Count(m => m.ClienteId is null);
    private int TotalDelCliente => _macros.Count(m => m.ClienteId is not null);

    private string NombreClienteCargado =>
        Guid.TryParse(_clienteFiltroCargado, out var id)
            ? _clientesSelector.FirstOrDefault(c => c.Id == id)?.RazonSocial ?? "este cliente"
            : string.Empty;

    /// <summary>
    /// Qué hay en la lista, contado sobre lo que devolvió la consulta. Sin
    /// cliente, todas son genéricas por construcción; con cliente, el filtro
    /// SUMA, y la frase lo dice. Sin punto final tras el nombre: las razones
    /// sociales acaban a menudo en «S.L.» y salía «S.L..».
    /// </summary>
    private string ResumenFiltro => string.IsNullOrEmpty(_clienteFiltroCargado)
        ? $"{Contar(TotalGenericas, "macro genérica", "macros genéricas")}. Elige un cliente para ver también las suyas."
        : $"{Contar(TotalGenericas, "genérica", "genéricas")} + {TotalDelCliente} de {NombreClienteCargado}";

    private string DesgloseFiltro
    {
        get
        {
            var genericas = Titulos(_macros.Where(m => m.ClienteId is null));
            return string.IsNullOrEmpty(_clienteFiltroCargado)
                ? $"Genéricas: {genericas}."
                : $"Genéricas: {genericas}. De {NombreClienteCargado}: {Titulos(_macros.Where(m => m.ClienteId is not null))}.";
        }
    }

    private string EtiquetaRecuento => string.IsNullOrEmpty(_clienteFiltroCargado)
        ? "macro(s) genérica(s)"
        : $"macro(s): {TotalGenericas} genérica(s) · {TotalDelCliente} de {NombreClienteCargado}";

    private static string Titulos(IEnumerable<MacroListaDto> macros)
    {
        var titulos = macros.Select(m => m.Titulo).ToList();
        return titulos.Count == 0 ? "ninguna" : string.Join(" · ", titulos);
    }

    private static string Contar(int cantidad, string singular, string plural) =>
        $"{cantidad} {(cantidad == 1 ? singular : plural)}";

    // ------------------------------------------------------------ paginación

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_macros.Count / (double)_tamanoPagina));

    private IEnumerable<MacroListaDto> MacrosPagina =>
        _macros.Skip((_paginaActual - 1) * _tamanoPagina).Take(_tamanoPagina);

    private void CambiarPagina(int pagina) => _paginaActual = Math.Clamp(pagina, 1, TotalPaginas);

    private void CambiarTamanoPagina(int tamano)
    {
        _tamanoPagina = tamano;
        _paginaActual = 1;
    }

    /// <summary>
    /// Segunda línea de cada fila: el contenido en texto corrido, sin
    /// etiquetas, recortado. <c>CuerpoHtml</c> llega ya en la fila, así que no
    /// hay consulta extra. Razor escapa el resultado al pintarlo.
    /// </summary>
    internal static string ResumenContenido(string? cuerpo)
    {
        // Las etiquetas que separan bloques dejan un espacio; las de línea
        // (<b>, <a>…) no, o «<b>recepción</b>.» saldría «recepción .».
        var separado = EtiquetaDeBloque().Replace(cuerpo ?? string.Empty, " ");
        var sinEtiquetas = EtiquetaHtml().Replace(separado, string.Empty);
        var texto = EspaciosSeguidos().Replace(WebUtility.HtmlDecode(sinEtiquetas), " ").Trim();
        return texto.Length <= LongitudResumen ? texto : texto[..LongitudResumen].TrimEnd() + "…";
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex EtiquetaDeBloque();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex EtiquetaHtml();

    [GeneratedRegex(@"\s+")]
    private static partial Regex EspaciosSeguidos();

    // ------------------------------------------------------------ formulario

    private void AbrirCrear()
    {
        _solicitudVersion++;
        _editandoId = null;
        _clienteIdOriginal = null;
        _titulo = string.Empty;
        _cuerpo = string.Empty;
        // Con un cliente elegido en el filtro, la macro nueva nace suya: es lo
        // que se está mirando. Se puede cambiar a genérica en el propio campo.
        _clienteIdFormulario = _clienteFiltro;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _conflicto = false;
        _recargandoVersion = false;
        _drawerVisible = true;
    }

    private void AbrirEditar(MacroListaDto macro)
    {
        _solicitudVersion++;
        _editandoId = macro.Id;
        _versionEditando = macro.Version;
        _clienteIdOriginal = macro.ClienteId;
        _titulo = macro.Titulo;
        _cuerpo = macro.CuerpoHtml;
        _clienteIdFormulario = macro.ClienteId?.ToString() ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _conflicto = false;
        _recargandoVersion = false;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        // Mientras el guardado está en vuelo el formulario no se cierra (el
        // aspa sigue viva aunque Cancelar esté deshabilitado): si se cerrase y
        // se abriera otra macro, el final del guardado anterior cerraría el
        // formulario nuevo.
        if (!visible && _guardando)
            return Task.CompletedTask;

        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        // Guarda de doble clic: Cargando deshabilita el botón, pero ese valor
        // solo llega al navegador con el siguiente render, y el segundo clic
        // puede entrar antes. La bandera se levanta antes del primer await.
        if (_guardando || _conflicto)
            return;

        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var clienteId = Guid.TryParse(_clienteIdFormulario, out var id) ? id : (Guid?)null;

            Error? error;
            if (_editandoId is null)
            {
                var resultado = await Mediator.Send(new CrearMacroCommand(_titulo, _cuerpo, clienteId));
                error = resultado.EsFallido ? resultado.Error : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarMacroCommand(_editandoId.Value, _titulo, _cuerpo, clienteId, _versionEditando));
                error = resultado.EsFallido ? resultado.Error : null;
            }

            if (error is not null)
            {
                if (error.Codigo == ConcurrenciaOptimista.CodigoConflicto)
                    _conflicto = true;
                else
                    _mensajeErrorFormulario = error.Mensaje;
                return;
            }

            ToastService.Mostrar(_editandoId is null ? "Macro creada correctamente." : "Macro actualizada correctamente.", TonoToast.Exito);
            _drawerVisible = false;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);

            // Si el error es de un campo que se ve, el mensaje de arriba solo
            // señala; si no (p. ej. el Id), es el único sitio donde se lee.
            _mensajeErrorFormulario = _erroresCampo.Keys.Any(EsCampoDelFormulario)
                ? "Revisa los campos marcados."
                : string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al guardar la macro {MacroId} (null si es alta).", _editandoId);
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private static bool EsCampoDelFormulario(string propiedad) =>
        propiedad is nameof(CrearMacroCommand.Titulo) or nameof(CrearMacroCommand.CuerpoHtml);

    /// <summary>
    /// Tras un conflicto, trae la versión que guardó la otra persona y la pone
    /// en el formulario. Se pide con el cliente que la macro tenía al abrirla:
    /// si la reasignaron a otro cliente, no vuelve en esa consulta y se dice.
    /// </summary>
    private async Task CargarVersionActualAsync()
    {
        if (_recargandoVersion || _editandoId is not { } id)
            return;

        var solicitud = ++_solicitudVersion;
        _recargandoVersion = true;
        var recargada = false;

        try
        {
            var macros = await Mediator.Send(new ObtenerMacrosQuery(_clienteIdOriginal));
            if (solicitud != _solicitudVersion || _editandoId != id) return;

            var actual = macros.FirstOrDefault(m => m.Id == id);
            if (actual is null)
            {
                _mensajeErrorFormulario =
                    "Esta macro ya no está donde la abriste: puede que la hayan eliminado o asignado a otro cliente. Cierra el formulario y búscala en la lista.";
                return;
            }

            _versionEditando = actual.Version;
            _clienteIdOriginal = actual.ClienteId;
            _titulo = actual.Titulo;
            _cuerpo = actual.CuerpoHtml;
            _clienteIdFormulario = actual.ClienteId?.ToString() ?? string.Empty;
            _erroresCampo = new Dictionary<string, string>();
            _mensajeErrorFormulario = null;
            _conflicto = false;
            recargada = true;
        }
        catch (Exception ex)
        {
            if (solicitud != _solicitudVersion) return;

            Logger.LogError(ex, "Error al recargar la versión actual de la macro {MacroId}.", id);
            _mensajeErrorFormulario = "No pudimos cargar la versión actual. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            if (solicitud == _solicitudVersion)
                _recargandoVersion = false;
        }

        // La fila de la lista también tiene la versión vieja.
        if (recargada)
            await CargarAsync();
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    // ------------------------------------------------------------ eliminar

    private void AbrirEliminar(Guid id, string titulo)
    {
        _idAEliminar = id;
        _tituloAEliminar = titulo;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        // DialogoConfirmacion ya trae su propia guarda de reentrada; esta es la
        // de la página, por si el comando se dispara por otro camino.
        if (_eliminando)
            return;

        _eliminando = true;

        try
        {
            // Guid.Empty no: atribuía el borrado a un usuario inexistente en
            // vez de fallar, y eso deja una pista falsa en la auditoría —
            // justo lo contrario de para qué existe (hallazgo N-13 de
            // INFORME-AUDITORIA-2.md).
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null)
            {
                ToastService.Mostrar("Tu sesión ha caducado. Vuelve a iniciar sesión.", TonoToast.Error);
                return;
            }

            var resultado = await Mediator.Send(new EliminarMacroCommand(_idAEliminar, usuarioId.Value));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Macro eliminada.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await CargarAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al eliminar la macro {MacroId}.", _idAEliminar);
            ToastService.Mostrar("No pudimos eliminar la macro. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
