using CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.TiposDocumento;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Documentos.Pages;

/// <summary>
/// Tipo de documento 360 — página (mockup «Tipo Documento 360 página TALVEG», primer incremento): el estado de un tipo de
/// documento en todos los Trabajadores a los que algún Centro se lo exige. Es de consulta: la configuración del tipo sigue
/// en <c>/tipos-documento</c> y el panel de Documento se conserva como consulta rápida (clic en el nombre de la fila).
///
/// <para>
/// Todo sale de una sola consulta, <see cref="ObtenerEstadoTipoDocumentoQuery"/>, que es también la que autoriza: quien no
/// puede leer la página recibe <c>null</c> y ve el mismo error que con un tipo que no existe. <see cref="RolesDeLaPagina"/>
/// repite esa lista en la ruta para que tampoco se llegue a montar el circuito.
/// </para>
///
/// <para>
/// Fuera de este incremento (lista de aceptación, § 2): pestañas «Centros que lo exigen», «Empresas» e «Historial», grupo
/// «Sin Centro que lo exija», «Reclamar pendientes», «Pendiente de subir» por plataforma, «Bloqueado», tolerancia en el
/// lateral y las variantes Empresa y Vehículo.
/// </para>
/// </summary>
public partial class TipoDocumentoDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>
    /// Todos los roles salvo Consulta y Cliente (decisión del 2026-10-08). Misma lista que
    /// <see cref="ObtenerEstadoTipoDocumentoQueryHandler.RolesQueVenLaPagina"/>; lo vigila
    /// <c>TipoDocumento360SoloRolesDeGestionTests</c>.
    /// </summary>
    internal const string RolesDeLaPagina =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae}";

    internal const string PestanaTrabajadores = "trabajadores";

    /// <summary>Nombres que la banda enseña por cada grupo de estado; el resto se resume en «y N más».</summary>
    private const int NombresPorParteDeBanda = 3;

    /// <summary>La banda de cabecera: sus partes («2 vencidos: …»), su tono y los contadores que marca su enlace.</summary>
    private sealed record Banda(
        TonoBanda Tono, IReadOnlyList<ParteDeBanda> Partes, string TextoEnlace, IReadOnlyList<GrupoEstadoTipoDocumento> Grupos);

    private sealed record ParteDeBanda(string Recuento, IReadOnlyList<FilaTrabajadorTipoDocumentoDto> Nombradas, int Resto);

    [Parameter] public Guid TipoDocumentoId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private DrawerGestionDocumento _drawerGestion = default!;

    private EstadoTipoDocumentoDto? _estado;
    private bool _cargando = true;
    private bool _error;
    private Guid _tipoCargado;
    private int _carga;

    private IReadOnlySet<string> _estadosMarcados = new HashSet<string>();
    private int _pagina = 1;
    private int _tamanoPagina = ObtenerEstadoTipoDocumentoQuery.TamanoPaginaPorDefecto;
    private readonly HashSet<Guid> _desplegadas = [];

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
    [
        new(Textos["MigaDocumentos"]),
        new(_estado?.Nombre ?? Textos["MigaCargando"])
    ];

    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaTrabajadores, Textos["PestanaTrabajadores"])
        {
            Contador = new ContadorPestana(_estado?.Trabajadores ?? 0, Textos["GlosaPestanaTrabajadores"])
        }
    ];

    private IReadOnlyList<OpcionEstadoRecuento> OpcionesDeEstado =>
        (_estado?.Recuentos ?? [])
        .Select(r => new OpcionEstadoRecuento(r.Grupo.ToString(), TextoGrupo(r.Grupo), r.Filas))
        .ToList();

    protected override async Task OnParametersSetAsync()
    {
        if (_tipoCargado == TipoDocumentoId)
            return;

        _tipoCargado = TipoDocumentoId;
        _estado = null;
        _estadosMarcados = new HashSet<string>();
        _pagina = 1;
        _desplegadas.Clear();
        await CargarAsync();
    }

    /// <summary>
    /// Carga (o recarga) la página con el filtro y la página actuales. Una respuesta que llega después de otra petición
    /// más nueva se descarta. Solo la primera carga enseña el esqueleto: al filtrar o paginar la lista anterior se queda
    /// hasta que llega la nueva.
    /// </summary>
    private async Task CargarAsync()
    {
        var carga = ++_carga;
        _cargando = _estado is null;
        _error = false;

        try
        {
            var estados = _estadosMarcados.Select(Enum.Parse<GrupoEstadoTipoDocumento>).ToList();
            var estado = await Mediator.Send(
                new ObtenerEstadoTipoDocumentoQuery(TipoDocumentoId, estados, _pagina, _tamanoPagina), _ciclo.Token);
            if (carga != _carga) return;

            // null = no existe, no se pide a Trabajadores o quien pregunta no puede leerla: la página no distingue.
            _estado = estado;
            _error = estado is null;
            if (estado is not null)
                _pagina = estado.Pagina;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (carga == _carga)
            {
                _estado = null;
                _error = true;
            }
        }
        finally
        {
            if (carga == _carga)
                _cargando = false;
        }
    }

    private async Task CambiarEstadosAsync(IReadOnlySet<string> marcados)
    {
        _estadosMarcados = marcados;
        _pagina = 1;
        await CargarAsync();
    }

    private Task VerDesdeLaBandaAsync(IEnumerable<GrupoEstadoTipoDocumento> grupos) =>
        CambiarEstadosAsync(grupos.Select(g => g.ToString()).ToHashSet());

    private async Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        await CargarAsync();
    }

    private async Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        await CargarAsync();
    }

    private void Desplegar(Guid trabajadorId, bool desplegada)
    {
        if (desplegada) _desplegadas.Add(trabajadorId);
        else _desplegadas.Remove(trabajadorId);
    }

    public void Dispose()
    {
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    // ----- Navegación y acciones -----

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/documentos");
    }

    private void IrAConfiguracionDelTipo() => NavigationManager.NavigateTo("/tipos-documento");

    private void IrAFichaDeTrabajador(Guid trabajadorId) => NavigationManager.NavigateTo($"/trabajadores/{trabajadorId}");

    /// <summary>Consulta rápida: el panel del Documento si la fila tiene uno y, si no, el del Trabajador.</summary>
    private Task AbrirPanelAsync(FilaTrabajadorTipoDocumentoDto fila) => fila.DocumentoId is { } documentoId
        ? WorkspaceService.AbrirAsync(EntidadWorkspace.Documento, documentoId, _estado?.Nombre ?? string.Empty, "informacion")
        : WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, fila.TrabajadorId, fila.Nombre, "informacion");

    /// <summary>
    /// La corrección en esta misma ficha: el formulario de documento con el documento de la fila (renovar, o fijar la
    /// fecha de uno sin confirmar) o, si no hay ninguno, el alta con Trabajador y tipo ya elegidos.
    /// </summary>
    private Task CorregirAsync(FilaTrabajadorTipoDocumentoDto fila) => fila.DocumentoId is { } documentoId
        ? _drawerGestion.AbrirEditarAsync(documentoId)
        : _drawerGestion.AbrirCrearParaFaltanteAsync(fila.TrabajadorId, TipoDocumentoId);

    // ----- Presentación -----

    /// <summary>
    /// Banda de peligro si hay filas vencidas o pendientes; si no, de advertencia con las que están en tolerancia; sin
    /// ninguna de las tres no hay banda. Cada nombre abre la corrección de esa fila; el enlace marca esos contadores.
    /// </summary>
    private Banda? BandaDeCabecera(EstadoTipoDocumentoDto estado)
    {
        var peligro = new[]
            {
                ParteDe(estado, GrupoEstadoTipoDocumento.Vencido, "BandaVencidosUno", "BandaVencidosVarios"),
                ParteDe(estado, GrupoEstadoTipoDocumento.Pendiente, "BandaPendientesUno", "BandaPendientesVarios")
            }
            .OfType<ParteDeBanda>()
            .ToList();
        if (peligro.Count > 0)
            return new Banda(TonoBanda.Peligro, peligro, Textos["BandaEnlacePeligro"],
                [GrupoEstadoTipoDocumento.Vencido, GrupoEstadoTipoDocumento.Pendiente]);

        return ParteDe(estado, GrupoEstadoTipoDocumento.EnTolerancia, "BandaToleranciaUno", "BandaToleranciaVarios") is { } tolerancia
            ? new Banda(TonoBanda.Advertencia, [tolerancia], Textos["BandaEnlaceTolerancia"], [GrupoEstadoTipoDocumento.EnTolerancia])
            : null;
    }

    private ParteDeBanda? ParteDe(EstadoTipoDocumentoDto estado, GrupoEstadoTipoDocumento grupo, string claveUno, string claveVarios)
    {
        var filas = estado.Recuentos.FirstOrDefault(r => r.Grupo == grupo)?.Filas ?? 0;
        if (filas == 0)
            return null;

        var nombradas = estado.Incidencias
            .Where(f => EstadoTipoDocumentoCalculo.Grupo(f.PeorEstado) == grupo)
            .Take(NombresPorParteDeBanda)
            .ToList();
        return new ParteDeBanda(Plural(filas, claveUno, claveVarios), nombradas, filas - nombradas.Count);
    }

    private static int TotalPaginas(EstadoTipoDocumentoDto estado) =>
        Math.Max(1, (int)Math.Ceiling(estado.TotalFiltradas / (double)estado.TamanoPagina));

    private string TextoSePide(RequisitoDocumental requerido) => requerido switch
    {
        RequisitoDocumental.Si => TextosTipos["RequeridoSi"],
        RequisitoDocumental.Condicional => TextosTipos["RequeridoCondicional"],
        _ => TextosTipos["RequeridoNo"]
    };

    /// <summary>Pastilla de exigencia de la cabecera: «Sí, siempre · Obligación legal»; sin autoridad si no se pide siempre.</summary>
    private string TextoExigencia(EstadoTipoDocumentoDto estado) => estado.Requerido == RequisitoDocumental.Si
        ? $"{TextoSePide(estado.Requerido)} · {RequisitoDocumentalUi.TextoNaturaleza(TextosTipos, estado.Naturaleza)}"
        : TextoSePide(estado.Requerido);

    private static TonoBadge TonoExigencia(EstadoTipoDocumentoDto estado) => estado.Requerido switch
    {
        RequisitoDocumental.Si => RequisitoDocumentalUi.Tono(estado.Naturaleza),
        RequisitoDocumental.Condicional => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    private static string Iniciales(string nombre) => string.Concat(nombre
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Take(2)
        .Select(palabra => char.ToUpperInvariant(palabra[0])));

    private string Plural(int cantidad, string claveUno, string claveVarios) =>
        Textos[cantidad == 1 ? claveUno : claveVarios, cantidad];

    private string TextoVigencia(int? meses) => meses switch
    {
        null => Textos["VigenciaSinDefinir"],
        1 => Textos["VigenciaMesesUno", 1],
        _ => Textos["VigenciaMesesVarios", meses]
    };

    private string TextoGrupo(GrupoEstadoTipoDocumento grupo) => Textos[$"Estado{grupo}"];

    private string TextoEstado(EstadoDocumento estado) => TextoGrupo(EstadoTipoDocumentoCalculo.Grupo(estado));

    private static TonoBadge TonoEstado(EstadoDocumento estado) => EstadoTipoDocumentoCalculo.Grupo(estado) switch
    {
        GrupoEstadoTipoDocumento.Vencido or GrupoEstadoTipoDocumento.Pendiente => TonoBadge.Peligro,
        GrupoEstadoTipoDocumento.EnTolerancia => TonoBadge.Tolerancia,
        GrupoEstadoTipoDocumento.PorVencer or GrupoEstadoTipoDocumento.SinConfirmar => TonoBadge.Advertencia,
        _ => TonoBadge.Exito
    };

    /// <summary>
    /// Fila con problema: peligro para Vencido y Pendiente; advertencia para En tolerancia y para Por vencer con pocos
    /// días (Urgente). Próximo, Sin confirmar y Vigente no tiñen.
    /// </summary>
    private static TonoFila? TonoDeFila(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.Faltante => TonoFila.Peligro,
        EstadoDocumento.EnTolerancia or EstadoDocumento.Urgente => TonoFila.Advertencia,
        _ => null
    };

    /// <summary>Rótulo de la acción de corrección según el estado; <c>null</c> si no hay nada que corregir.</summary>
    private string? TextoAccion(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia or EstadoDocumento.Urgente => Texto("AccionRenovar"),
        EstadoDocumento.Faltante => Texto("AccionSubir"),
        EstadoDocumento.SinConfirmar => Texto("AccionConfirmar"),
        _ => null
    };

    private string Texto(string clave, params object[] argumentos) => Textos[clave, argumentos].Value;

    private static string Fecha(DateOnly fecha) => fecha.ToString("dd/MM/yyyy");

    /// <summary>La situación del documento en un Centro, en una frase.</summary>
    private string TextoEnCentro(FilaTrabajadorTipoDocumentoDto fila, EstadoEnCentroDto centro)
    {
        var vence = fila.FechaVencimiento;
        return centro.Estado switch
        {
            EstadoDocumento.Faltante => Texto("CentroNoHayDocumento"),
            EstadoDocumento.SinConfirmar => Texto("CentroSinFecha"),
            EstadoDocumento.SinCaducidad => Texto("CentroNoCaduca"),
            EstadoDocumento.EnTolerancia when vence is { } v && centro.EnToleranciaHasta is { } hasta =>
                Texto("CentroEnTolerancia", Fecha(v), hasta.ToString("dd/MM")),
            EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia when vence is { } v => Texto("CentroVencio", Fecha(v)),
            EstadoDocumento.Urgente or EstadoDocumento.Proximo when vence is { } v => (v.DayNumber - DiaDeNegocio.Hoy().DayNumber) switch
            {
                <= 0 => Texto("CentroCaducaHoy"),
                1 => Texto("CentroCaducaManana"),
                var dias => Texto("CentroCaducaEnDias", dias)
            },
            _ when vence is { } v => Texto("CentroVigenteHasta", Fecha(v)),
            _ => TextoEstado(centro.Estado)
        };
    }
}
