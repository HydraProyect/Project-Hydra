using System.Globalization;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Pages;

/// <summary>
/// «Reclamar para acceso» de una Empresa (<c>/empresas/{EmpresaId}/reclamar-acceso</c>), contra su maqueta
/// «Reclamacion por acceso TALVEG.dc.html». Elegido un Centro y los Trabajadores que van a entrar, enseña lo que ese Centro
/// exige y la Empresa tiene vencido o pendiente (<see cref="ObtenerReclamacionPorAccesoQuery"/>, la única fuente), a quién iría
/// cada tarjeta y deja abrir el cajón de «Reclamar en lote» ya en su revisión. No envía nada por sí misma.
///
/// <para>
/// Los Centros y los Trabajadores que se ofrecen salen de <see cref="ObtenerReclamacionPreventivaEmpresaQuery"/> (su lista
/// <c>Centros</c>: Asignación activa, Centro con gestión CAE, todo dentro del alcance visible); aquí solo se usa esa lista.
/// </para>
///
/// <para>
/// <b>Reglas de carrera:</b> la carga de la Empresa y la de los lotes llevan cada una su versión, capturada antes de cada
/// <c>await</c>: una respuesta de una petición que ya no es la vigente (otro Centro, otra selección, un reintento, otra
/// Empresa) se descarta. La retirada de la página cancela lo que viaja y no pinta tras ella.
/// </para>
/// </summary>
public partial class ReclamarParaAcceso : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    [Parameter] public Guid EmpresaId { get; set; }

    /// <summary>Centro 360 (y las demás entradas) llegan con el Centro ya elegido: <c>?centroId=</c>.</summary>
    [SupplyParameterFromQuery(Name = "centroId")]
    public Guid? CentroInicial { get; set; }

    /// <summary>Trabajador 360 llega con ese Trabajador preseleccionado: <c>?trabajadorId=</c>.</summary>
    [SupplyParameterFromQuery(Name = "trabajadorId")]
    public Guid? TrabajadorInicial { get; set; }

    private sealed record FilaReclamable(string Id, string Nombre, string Detalle, TonoBadge Tono, string Insignia);

    private sealed record GrupoTrabajador(Guid TrabajadorId, string Nombre, IReadOnlyList<FilaReclamable> Filas);

    private EmpresaDetalleDto? _empresa;
    private IReadOnlyList<CentroConTrabajadoresDto> _centros = [];
    private Guid? _centroId;
    private readonly HashSet<Guid> _seleccion = [];

    private IReadOnlyList<LoteReclamacionAgrupadoDto> _lotes = [];
    private IReadOnlyList<FilaReclamable> _filasEmpresa = [];
    private IReadOnlyList<GrupoTrabajador> _gruposTrabajadores = [];

    private bool _noEncontrada;
    private bool _errorCarga;
    private bool _errorLotes;
    private bool _calculando;
    private bool _lotesCalculados;
    private bool _reclamacionVisible;

    private int _versionCarga;
    private int _versionLotes;
    private Guid? _empresaCargada;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    private IReadOnlyList<OpcionFiltroDto> TrabajadoresDelCentro =>
        _centros.FirstOrDefault(c => c.CentroId == _centroId)?.Trabajadores ?? [];

    private string NombreCentro => _centros.FirstOrDefault(c => c.CentroId == _centroId)?.CentroNombre ?? string.Empty;

    private int TotalDocumentosDeTrabajadores => _gruposTrabajadores.Sum(g => g.Filas.Count);

    private bool HayDestinatario => _lotes.Any(l => l.Destinatarios is { Count: > 0 });

    /// <summary>El cajón copia los lotes al abrirse: con una consulta en vuelo lo que se ve ya no es lo que se pidió, y no se ofrece reclamarlo.</summary>
    private bool PuedeReclamar => HayDestinatario && !_calculando && _lotesCalculados;

    /// <summary>La carga va aquí y no en <c>OnInitializedAsync</c>: navegar de una Empresa a otra reutiliza el componente.</summary>
    protected override Task OnParametersSetAsync()
    {
        if (_empresaCargada == EmpresaId)
            return Task.CompletedTask;

        _empresaCargada = EmpresaId;
        _versionLotes++;
        _calculando = false;
        _empresa = null;
        _centros = [];
        _centroId = null;
        _seleccion.Clear();
        _lotes = [];
        _filasEmpresa = [];
        _gruposTrabajadores = [];
        _lotesCalculados = false;
        _errorLotes = false;
        _reclamacionVisible = false;
        return CargarAsync();
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private bool EsVigente(int version) => !_desechado && version == _versionCarga;

    private bool EsVigenteLotes(int version) => !_desechado && version == _versionLotes;

    private Task ReintentarAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        var version = ++_versionCarga;
        var token = _ciclo.Token;
        _errorCarga = false;
        _noEncontrada = false;
        StateHasChanged();

        try
        {
            var empresa = await Mediator.Send(new ObtenerEmpresaPorIdQuery(EmpresaId), token);
            if (!EsVigente(version))
                return;

            if (empresa is null)
            {
                _noEncontrada = true;
                return;
            }

            var preventiva = await Mediator.Send(new ObtenerReclamacionPreventivaEmpresaQuery(EmpresaId), token);
            if (!EsVigente(version))
                return;

            _empresa = empresa;
            _centros = preventiva.Centros;
            _centroId = CentroInicial is { } inicial && _centros.Any(c => c.CentroId == inicial)
                ? inicial
                : _centros.FirstOrDefault()?.CentroId;
            SeleccionarInicial();
        }
        catch (OperationCanceledException) when (_desechado)
        {
            return;
        }
        catch (Exception)
        {
            if (!EsVigente(version))
                return;

            _errorCarga = true;
            return;
        }

        await RecalcularAsync();
    }

    /// <summary>Con <c>?trabajadorId=</c> de ese Centro, solo ese Trabajador; si no, todos los de la Empresa en el Centro.</summary>
    private void SeleccionarInicial()
    {
        _seleccion.Clear();
        var delCentro = TrabajadoresDelCentro;
        if (TrabajadorInicial is { } trabajador && delCentro.Any(t => t.Id == trabajador))
            _seleccion.Add(trabajador);
        else
            _seleccion.UnionWith(delCentro.Select(t => t.Id));
    }

    private async Task CambiarCentroAsync(string valor)
    {
        if (!Guid.TryParse(valor, out var centro) || centro == _centroId || _centros.All(c => c.CentroId != centro))
            return;

        _centroId = centro;
        _seleccion.Clear();
        _seleccion.UnionWith(TrabajadoresDelCentro.Select(t => t.Id));

        // Lo del Centro anterior no se queda bajo el nombre del nuevo mientras llega la respuesta.
        _lotes = [];
        _lotesCalculados = false;
        ConstruirFilas();
        await RecalcularAsync();
    }

    private async Task AlternarTrabajadorAsync(Guid trabajadorId, bool marcado)
    {
        if (marcado)
            _seleccion.Add(trabajadorId);
        else
            _seleccion.Remove(trabajadorId);

        await RecalcularAsync();
    }

    /// <summary>Un manejador por Trabajador, construido aquí para no meter una lambda en el marcado.</summary>
    private EventCallback<ChangeEventArgs> AlAlternarTrabajador(Guid trabajadorId) =>
        EventCallback.Factory.Create<ChangeEventArgs>(this, e => AlternarTrabajadorAsync(trabajadorId, (bool)e.Value!));

    private void CambiarReclamacionVisible(bool visible) => _reclamacionVisible = visible;

    private Task SeleccionarTodosAsync()
    {
        _seleccion.UnionWith(TrabajadoresDelCentro.Select(t => t.Id));
        return RecalcularAsync();
    }

    private Task QuitarSeleccionAsync()
    {
        _seleccion.Clear();
        return RecalcularAsync();
    }

    /// <summary>Vuelve a pedir lo que exige el Centro para la selección actual (también tras un envío desde el cajón).</summary>
    private async Task RecalcularAsync()
    {
        if (_desechado)
            return;

        var version = ++_versionLotes;
        var token = _ciclo.Token;
        _errorLotes = false;

        if (_centroId is not { } centro || _seleccion.Count == 0)
        {
            _lotes = [];
            _lotesCalculados = false;
            _calculando = false;
            ConstruirFilas();
            return;
        }

        _calculando = true;
        StateHasChanged();

        try
        {
            var lotes = await Mediator.Send(new ObtenerReclamacionPorAccesoQuery(EmpresaId, centro, [.. _seleccion]), token);
            if (!EsVigenteLotes(version))
                return;

            _lotes = lotes;
            _lotesCalculados = true;
            ConstruirFilas();
        }
        catch (OperationCanceledException) when (_desechado)
        {
            return;
        }
        catch (Exception)
        {
            if (!EsVigenteLotes(version))
                return;

            _errorLotes = true;
        }
        finally
        {
            if (EsVigenteLotes(version))
                _calculando = false;
        }
    }

    private void ConstruirFilas()
    {
        var deEmpresa = new List<FilaReclamable>();
        var deTrabajadores = new Dictionary<Guid, (string Nombre, List<FilaReclamable> Filas)>();

        void Anadir(Guid? trabajadorId, string? trabajadorNombre, FilaReclamable fila)
        {
            if (trabajadorId is not { } id)
            {
                deEmpresa.Add(fila);
                return;
            }

            if (!deTrabajadores.TryGetValue(id, out var grupo))
                deTrabajadores[id] = grupo = (trabajadorNombre ?? string.Empty, []);

            grupo.Filas.Add(fila);
        }

        foreach (var lote in _lotes)
        {
            foreach (var documento in lote.Documentos)
            {
                var vencimiento = documento.FechaVencimiento.ToString(CultureInfo.CurrentCulture);
                Anadir(documento.TrabajadorId, documento.TrabajadorNombre, new FilaReclamable(
                    $"doc:{documento.DocumentoId}", documento.TipoDocumentoNombre, Textos["DocumentoVencimiento", vencimiento],
                    EstadoDocumentoUi.Tono(documento.Estado), EstadoDocumentoUi.Texto(documento.Estado)));
            }

            foreach (var pendiente in lote.PendientesSinFecha ?? [])
            {
                var sinConfirmar = pendiente.Motivo == MotivoPendienteDeReclamacion.SinConfirmar;
                Anadir(pendiente.TrabajadorId, pendiente.TrabajadorNombre, new FilaReclamable(
                    pendiente.Clave, pendiente.TipoDocumentoNombre,
                    Textos[sinConfirmar ? "DetalleSinConfirmar" : "DetalleFalta"],
                    sinConfirmar ? TonoBadge.Advertencia : TonoBadge.Peligro,
                    Textos[sinConfirmar ? "InsigniaSinConfirmar" : "InsigniaFalta"]));
            }
        }

        _filasEmpresa = deEmpresa;
        _gruposTrabajadores = [.. deTrabajadores
            .OrderBy(g => g.Value.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new GrupoTrabajador(g.Key, g.Value.Nombre, g.Value.Filas))];
    }

    private string Recuento(int total) => total == 1 ? Textos["RecuentoUno"] : Textos["RecuentoVarios", total];

    /// <summary>La Delegación a cuyo contacto va el lote; null si va a la agenda de la Empresa.</summary>
    private static string? DelegacionEmpresaDe(LoteReclamacionAgrupadoDto lote) => lote.EmpresaEmpleadora?.DelegacionEmpresaNombre;

    private string TituloDestino(LoteReclamacionAgrupadoDto lote) =>
        DelegacionEmpresaDe(lote) is { } delegacion
            ? Textos["DestinoDelegacion", delegacion]
            : Textos["DestinoAgenda"];

    /// <summary>Un lote sin destinatario: sin contacto, o con un contacto de Delegación que el usuario no puede ver.</summary>
    private string TextoSinContacto(LoteReclamacionAgrupadoDto lote) =>
        Textos[DelegacionEmpresaDe(lote) is null ? "DestinoSinContacto" : "DestinoDelegacionSinContacto"];

    private void AbrirReclamacion()
    {
        if (PuedeReclamar)
            _reclamacionVisible = true;
    }
}
