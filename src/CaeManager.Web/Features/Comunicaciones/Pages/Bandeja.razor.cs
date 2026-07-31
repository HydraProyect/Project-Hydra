using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;
using CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;
using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Infrastructure.Autorizacion;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

public record EjecutivoSelectorDto(Guid Id, string NombreCompleto);

public partial class Bandeja : ComponentBase
{
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private ILogger<Bandeja> Logger { get; set; } = default!;

    // --- Filtros ---
    private string _estadoFiltro = string.Empty;
    private string _mesFiltro = string.Empty; // input type="month" -> "yyyy-MM"
    private string _clienteIdFiltro = string.Empty;
    private bool _soloAsignadasAMi;
    private bool _soloSinAsignar;
    private string _busqueda = string.Empty;

    private bool _cargandoLista = true;
    private bool _errorCargaLista;
    private IReadOnlyList<ConversacionListaDto> _conversaciones = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<EjecutivoSelectorDto> _ejecutivosDisponibles = [];

    private readonly HashSet<string> _gruposColapsados = [];

    // --- Detalle / centro ---
    private Guid? _conversacionSeleccionadaId;
    private ConversacionDetalleDto? _detalle;
    private bool _cargandoDetalle;
    private ClienteDetalleDto? _clienteActivo;
    private IReadOnlyList<MacroListaDto> _macrosDisponibles = [];

    private string _textoRespuesta = string.Empty;
    private string _macroSeleccionadaId = string.Empty;
    private bool _enviandoRespuesta;
    private string _ejecutivoSeleccionado = string.Empty;
    private bool _cambiandoEjecutivo;
    private bool _cambiandoEstado;

    private string _clienteTriageSeleccionado = string.Empty;
    private bool _asignandoCliente;

    protected override async Task OnInitializedAsync()
    {
        _clientesSelector = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        // Acotado al tenant activo: GetUsersInRoleAsync devuelve los gestores
        // de todas las organizaciones (AspNetUsers no tiene filtro global),
        // así que el selector listaba nombres de empleados de otros tenants.
        var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
        _ejecutivosDisponibles = gestores
            .Select(u => new EjecutivoSelectorDto(u.Id, u.NombreCompleto))
            .ToList();

        await CargarListaAsync();
    }

    private async Task CargarListaAsync()
    {
        _cargandoLista = true;
        _errorCargaLista = false;
        StateHasChanged();

        try
        {
            // El input HTML type="month" entrega "yyyy-MM" — se parsea a mano
            // para no depender de que el formato coincida con la cultura actual.
            int? anio = null;
            int? mes = null;
            var partesMes = _mesFiltro.Split('-');
            if (partesMes.Length == 2 && int.TryParse(partesMes[0], out var anioParseado) && int.TryParse(partesMes[1], out var mesParseado))
            {
                anio = anioParseado;
                mes = mesParseado;
            }

            _conversaciones = await Mediator.Send(new ObtenerConversacionesQuery(
                Estado: string.IsNullOrEmpty(_estadoFiltro) ? null : Enum.Parse<EstadoConversacion>(_estadoFiltro),
                Anio: anio,
                Mes: mes,
                ClienteId: Guid.TryParse(_clienteIdFiltro, out var clienteId) ? clienteId : null,
                SoloAsignadasAMi: _soloAsignadasAMi,
                SoloSinAsignar: _soloSinAsignar,
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda));
        }
        catch (Exception ex)
        {
            // _errorCargaLista solo pinta un aviso genérico: sin este log, un
            // fallo al cargar la bandeja no deja ningún rastro que permita
            // diagnosticarlo después.
            Logger.LogError(ex, "Error al cargar la lista de conversaciones de la bandeja.");
            _errorCargaLista = true;
        }
        finally
        {
            _cargandoLista = false;
            StateHasChanged();
        }
    }

    private Task AplicarFiltrosAsync() => CargarListaAsync();

    private Task FiltrarAsignadasAMiAsync()
    {
        _soloAsignadasAMi = !_soloAsignadasAMi;
        if (_soloAsignadasAMi) _soloSinAsignar = false;
        return CargarListaAsync();
    }

    private Task FiltrarSinAsignarAsync()
    {
        _soloSinAsignar = !_soloSinAsignar;
        if (_soloSinAsignar) _soloAsignadasAMi = false;
        return CargarListaAsync();
    }

    private Task VerTodasAsync()
    {
        _soloAsignadasAMi = false;
        _soloSinAsignar = false;
        return CargarListaAsync();
    }

    private IEnumerable<IGrouping<Guid, ConversacionListaDto>> GruposPorCliente() =>
        _conversaciones.Where(c => c.ClienteId is not null).GroupBy(c => c.ClienteId!.Value);

    private IReadOnlyList<ConversacionListaDto> ConversacionesTriage() =>
        _conversaciones.Where(c => c.ClienteId is null).OrderByDescending(c => c.FechaUltimoMensajeUtc).ToList();

    private void AlternarGrupo(string clave)
    {
        if (!_gruposColapsados.Add(clave))
            _gruposColapsados.Remove(clave);
    }

    private bool GrupoColapsado(string clave) => _gruposColapsados.Contains(clave);

    private async Task SeleccionarConversacionAsync(Guid id)
    {
        _conversacionSeleccionadaId = id;
        _cargandoDetalle = true;
        _textoRespuesta = string.Empty;
        _macroSeleccionadaId = string.Empty;
        _clienteTriageSeleccionado = string.Empty;
        StateHasChanged();

        try
        {
            _detalle = await Mediator.Send(new ObtenerConversacionPorIdQuery(id));
            _ejecutivoSeleccionado = _detalle?.EjecutivoAsignadoId?.ToString() ?? string.Empty;

            if (_detalle?.ClienteId is not null)
            {
                _clienteActivo = await Mediator.Send(new ObtenerClientePorIdQuery(_detalle.ClienteId.Value));
                _macrosDisponibles = await Mediator.Send(new ObtenerMacrosQuery(_detalle.ClienteId));
            }
            else
            {
                _clienteActivo = null;
                _macrosDisponibles = [];
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al abrir la conversación {ConversacionId}.", id);
            ToastService.Mostrar("No pudimos abrir esta conversación. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _cargandoDetalle = false;
            StateHasChanged();
        }
    }

    private void AplicarMacro(string macroIdTexto)
    {
        _macroSeleccionadaId = macroIdTexto;
        if (Guid.TryParse(macroIdTexto, out var macroId))
        {
            var macro = _macrosDisponibles.FirstOrDefault(m => m.Id == macroId);
            if (macro is not null)
                _textoRespuesta = macro.CuerpoHtml;
        }
    }

    private async Task EnviarRespuestaAsync()
    {
        if (_conversacionSeleccionadaId is null || string.IsNullOrWhiteSpace(_textoRespuesta)) return;

        _enviandoRespuesta = true;
        try
        {
            var resultado = await Mediator.Send(new ResponderConversacionCommand(_conversacionSeleccionadaId.Value, _textoRespuesta));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _textoRespuesta = string.Empty;
            _macroSeleccionadaId = string.Empty;
            ToastService.Mostrar("Respuesta enviada.", TonoToast.Exito);

            await SeleccionarConversacionAsync(_conversacionSeleccionadaId.Value);
            await CargarListaAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al responder en la conversación {ConversacionId}.", _conversacionSeleccionadaId);
            ToastService.Mostrar("No pudimos enviar la respuesta. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _enviandoRespuesta = false;
        }
    }

    private async Task CambiarEstadoAsync(EstadoConversacion nuevoEstado)
    {
        if (_conversacionSeleccionadaId is null) return;

        _cambiandoEstado = true;
        try
        {
            var resultado = await Mediator.Send(new CambiarEstadoConversacionCommand(_conversacionSeleccionadaId.Value, nuevoEstado));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await SeleccionarConversacionAsync(_conversacionSeleccionadaId.Value);
            await CargarListaAsync();
        }
        finally
        {
            _cambiandoEstado = false;
        }
    }

    private async Task CambiarEjecutivoAsync(string ejecutivoIdTexto)
    {
        _ejecutivoSeleccionado = ejecutivoIdTexto;
        if (_conversacionSeleccionadaId is null) return;

        _cambiandoEjecutivo = true;
        try
        {
            var ejecutivoId = Guid.TryParse(ejecutivoIdTexto, out var id) ? id : (Guid?)null;
            var resultado = await Mediator.Send(new AsignarEjecutivoConversacionCommand(_conversacionSeleccionadaId.Value, ejecutivoId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarListaAsync();
        }
        finally
        {
            _cambiandoEjecutivo = false;
        }
    }

    private async Task AsignarClienteTriageAsync()
    {
        if (_conversacionSeleccionadaId is null || !Guid.TryParse(_clienteTriageSeleccionado, out var clienteId)) return;

        _asignandoCliente = true;
        try
        {
            var resultado = await Mediator.Send(new AsignarClienteConversacionCommand(_conversacionSeleccionadaId.Value, clienteId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Conversación asignada al cliente.", TonoToast.Exito);
            await SeleccionarConversacionAsync(_conversacionSeleccionadaId.Value);
            await CargarListaAsync();
        }
        finally
        {
            _asignandoCliente = false;
        }
    }

    private static TonoBadge TonoBadgeDeEstado(EstadoConversacion estado) => estado switch
    {
        EstadoConversacion.Abierta => TonoBadge.Info,
        EstadoConversacion.Pendiente => TonoBadge.Info,
        _ => TonoBadge.Neutro
    };

    private static string FormatearFechaRelativa(DateTime fechaUtc)
    {
        var transcurrido = DateTime.UtcNow - fechaUtc;

        if (transcurrido.TotalMinutes < 1) return "ahora";
        if (transcurrido.TotalMinutes < 60) return $"hace {(int)transcurrido.TotalMinutes} min";
        if (transcurrido.TotalHours < 24) return $"hace {(int)transcurrido.TotalHours} h";
        if (transcurrido.TotalDays < 30) return $"hace {(int)transcurrido.TotalDays} d";

        return fechaUtc.ToString("dd/MM/yyyy");
    }
}
