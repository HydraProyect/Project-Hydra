using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Alertas.Pages;

public partial class Alertas : ComponentBase
{
    private int _tamanoPagina = 20;

    /// <summary>
    /// Ámbitos que la reclamación agregada de esta página ofrece en
    /// <c>SelectorLoteDocumental.AmbitosDisponibles</c> (DEC-4: reclamación
    /// agregada por entidad, trabajador <b>o empresa</b>). Trabajador y
    /// Empresa, que son los dos con camino de reclamación completo detrás
    /// (dominio, agenda, lote y envío — DEC-11: primero el camino, después la
    /// superficie).
    ///
    /// Cliente, Vehículo y Proyecto siguen fuera:
    /// <see cref="ObtenerLoteReclamacionPorFiltroQueryHandler"/> lanza
    /// <see cref="NotSupportedException"/> para ellos a propósito, y
    /// ofrecerlos aquí sería una promesa navegable sin capacidad detrás
    /// (A-08). <c>AlertasTests</c> protege que esta lista no se amplíe a
    /// ninguno de los tres antes de que exista ese camino.
    /// </summary>
    public static readonly IReadOnlyList<AmbitoAplicacion> AmbitosSoportados =
        [AmbitoAplicacion.Trabajador, AmbitoAplicacion.Empresa];

    /// <summary>
    /// Cómo se parte la lista en bloques. No filtra: las mismas alertas, en
    /// otro orden y bajo otra cabecera.
    /// </summary>
    private enum ModoAgrupacion
    {
        /// <summary>Un bloque por estado del documento, del más grave al menos grave.</summary>
        Severidad,

        /// <summary>Un bloque por tipo de documento — "qué hay que pedir", para reclamar en bloque.</summary>
        Motivo
    }

    /// <summary>Un bloque de la lista con TODAS sus alertas filtradas, no solo las de la página visible.</summary>
    private sealed record GrupoAlertas(
        string Clave,
        string Titulo,
        string Insignia,
        string? TituloInsignia,
        TonoBadge Tono,
        bool Critico,
        string? Descripcion,
        IReadOnlyList<AlertaDto> Alertas);

    /// <summary>Lo que el diálogo de confirmación anunció: se envía exactamente esto, no lo que haya en pantalla al confirmar.</summary>
    private sealed record EnvioPendiente(
        Guid TitularId,
        string TitularNombre,
        AmbitoAplicacion Ambito,
        IReadOnlyList<Guid> DocumentoIds,
        IReadOnlyList<Guid> ContactoIds,
        IReadOnlyList<string> NombresContactos);

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>
    /// Permite llegar aquí desde el Dashboard con el filtro de Estado ya
    /// aplicado (p. ej. la tarjeta KPI "Documentos vencidos" enlaza a
    /// "/alertas?estado=Vencido").
    /// </summary>
    [SupplyParameterFromQuery] public string? Estado { get; set; }

    private IReadOnlyList<AlertaDto> _alertas = [];
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;
    private int _versionCarga;

    private ModoAgrupacion _agrupacion = ModoAgrupacion.Severidad;
    private readonly HashSet<string> _gruposCerrados = [];

    private FiltroLoteDocumental? _filtroLote;
    private bool _cargandoLote;
    private bool _errorLote;
    private int _versionLote;
    private IReadOnlyList<LoteReclamacionAgrupadoDto> _lotes = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _seleccionPorTitular = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _contactosPorTitular = [];
    private Guid? _enviandoTitularId;
    private EnvioPendiente? _envioPendiente;
    private bool _altaContactoVisible;
    private Guid _titularAltaContacto;
    private TipoPropietarioAgenda _tipoAgendaAltaContacto = TipoPropietarioAgenda.Cliente;

    private IReadOnlyList<AlertaDto> AlertasFiltradas =>
        Enum.TryParse<EstadoDocumento>(_estadoFiltro, out var estado)
            ? _alertas.Where(a => a.Estado == estado).ToList()
            : _alertas;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(AlertasFiltradas.Count / (double)_tamanoPagina));

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página, no solo
    /// en el primer render, para que la URL sea la fuente de verdad del
    /// filtro (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _estadoFiltro = !string.IsNullOrWhiteSpace(Estado) && Enum.TryParse<EstadoDocumento>(Estado, out _)
            ? Estado
            : string.Empty;
    }

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private Task CambiarEstadoFiltroAsync(string valor)
    {
        _estadoFiltro = valor;
        _pagina = 1;
        NavigationManager.ActualizarFiltroEnUrl(nameof(Estado), valor);
        return Task.CompletedTask;
    }

    /// <summary>
    /// El único filtro de la lista. Separa "nada que reclamar" —una buena
    /// noticia— de "ninguna con este estado", que puede convivir con una
    /// docena de documentos vencidos. Agrupar no cuenta: no quita nada.
    /// </summary>
    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_estadoFiltro);

    // La guarda de la plantilla pide ADEMÁS que _alertas tenga algo. No sobra:
    // con cero alertas y un filtro puesto, "ninguna con este estado, hay 0 en
    // otros" es cierto y completamente inútil — lo que el usuario quiere leer
    // ahí es la buena noticia, "nada que reclamar". Lo destapó el propio test
    // al escribirlo desde la conducta esperada, no desde el código ya escrito.

    private Task LimpiarFiltrosAsync() => CambiarEstadoFiltroAsync(string.Empty);

    // ------------------------------------------------------------ Agrupación

    /// <summary>
    /// Orden de gravedad de los bloques (mockup Gen 2): primero lo que ya
    /// venció, luego lo que falta, luego lo que está por vencer. Un estado
    /// que ObtenerAlertasQuery no devuelve hoy va al final, con su propio
    /// bloque rotulado por <see cref="EstadoDocumentoUi"/> — nunca se pierde.
    /// </summary>
    private static int RangoSeveridad(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => 0,
        EstadoDocumento.Faltante => 1,
        EstadoDocumento.Urgente => 2,
        EstadoDocumento.Proximo => 3,
        _ => 4
    };

    /// <summary>
    /// Qué significa cada bloque, dicho solo con lo que la pantalla sabe. No
    /// afirma consecuencias (el mockup decía que un vencido "bloquea el acceso
    /// hoy": eso depende de cada centro y aquí no se sabe), ni nombra los días
    /// de los umbrales: son parámetros del sistema que AlertaDto no trae.
    /// </summary>
    private static string? DescripcionSeveridad(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => "El documento existe, pero su vigencia ya terminó.",
        // Dos procedencias, como el párrafo de procedencia de Alertas.razor:
        // la configuración del centro o, si no la hay, el valor general del tipo.
        EstadoDocumento.Faltante => "Se pide en un centro al que está asignado —porque ese centro lo tiene configurado o, si el centro no dice nada, porque el tipo de documento se pide siempre— y no hay ningún documento de ese tipo.",
        EstadoDocumento.Urgente => "Vence dentro del umbral corto de aviso.",
        EstadoDocumento.Proximo => "Vence dentro del umbral largo de aviso: hay margen para anticiparse.",
        _ => null
    };

    private IReadOnlyList<GrupoAlertas> Agrupar(IReadOnlyList<AlertaDto> alertas) =>
        _agrupacion == ModoAgrupacion.Severidad
            ? alertas
                .GroupBy(a => a.Estado)
                .OrderBy(g => RangoSeveridad(g.Key))
                .Select(g => new GrupoAlertas(
                    Clave: $"estado:{g.Key}",
                    Titulo: EstadoDocumentoUi.Texto(g.Key),
                    Insignia: g.Count().ToString(),
                    TituloInsignia: $"{g.Count()} alerta(s) en severidad {EstadoDocumentoUi.Texto(g.Key)}",
                    Tono: EstadoDocumentoUi.Tono(g.Key),
                    Critico: g.Key is EstadoDocumento.Vencido or EstadoDocumento.Faltante,
                    Descripcion: DescripcionSeveridad(g.Key),
                    Alertas: g.ToList()))
                .ToList()
            : alertas
                .GroupBy(a => a.TipoDocumentoId)
                .Select(g =>
                {
                    var peor = g.Select(a => a.Estado).MinBy(RangoSeveridad);
                    return new GrupoAlertas(
                        Clave: $"tipo:{g.Key}",
                        Titulo: g.First().TipoDocumentoNombre,
                        Insignia: EstadoDocumentoUi.Texto(peor),
                        TituloInsignia: $"Peor severidad del bloque: {EstadoDocumentoUi.Texto(peor)}",
                        Tono: EstadoDocumentoUi.Tono(peor),
                        Critico: false,
                        Descripcion: null,
                        Alertas: g.OrderBy(a => RangoSeveridad(a.Estado)).ToList());
                })
                .OrderBy(g => RangoSeveridad(g.Alertas[0].Estado))
                .ThenBy(g => g.Titulo, StringComparer.CurrentCulture)
                .ToList();

    /// <summary>
    /// La paginación recorre la lista YA agrupada: los bloques se cortan por
    /// la página, no la página por los bloques. Cada bloque conserva el total
    /// de sus alertas filtradas (su insignia) aunque en esta página solo se
    /// vea una parte — ver <see cref="MetaDeGrupo"/>.
    /// </summary>
    private IReadOnlyList<(GrupoAlertas Grupo, IReadOnlyList<AlertaDto> Filas)> GruposDePagina
    {
        get
        {
            var inicio = (_pagina - 1) * _tamanoPagina;
            var fin = inicio + _tamanoPagina;
            var desplazamiento = 0;
            var resultado = new List<(GrupoAlertas, IReadOnlyList<AlertaDto>)>();

            foreach (var grupo in Agrupar(AlertasFiltradas))
            {
                var desde = Math.Max(inicio - desplazamiento, 0);
                var hasta = Math.Min(fin - desplazamiento, grupo.Alertas.Count);
                if (hasta > desde)
                    resultado.Add((grupo, grupo.Alertas.Skip(desde).Take(hasta - desde).ToList()));
                desplazamiento += grupo.Alertas.Count;
            }

            return resultado;
        }
    }

    private static string MetaDeGrupo(GrupoAlertas grupo, int filasEnPagina)
    {
        var total = grupo.Alertas.Count;
        var base_ = grupo.Descripcion ?? $"{total} alerta(s)";
        return filasEnPagina < total ? $"{base_} · {filasEnPagina} en esta página" : base_;
    }

    private void CambiarAgrupacion(ModoAgrupacion modo)
    {
        if (_agrupacion == modo) return;
        _agrupacion = modo;
        _gruposCerrados.Clear();
        _pagina = 1;
    }

    private void AlternarGrupo(string clave)
    {
        if (!_gruposCerrados.Remove(clave))
            _gruposCerrados.Add(clave);
    }

    /// <summary>
    /// Un documento faltante (P1-15) no tiene DocumentoId — no hay nada que
    /// "gestionar" todavía. Lleva al drawer de creación con el propietario y
    /// el tipo ya elegidos en vez de a un documento inexistente.
    /// </summary>
    private void GestionarAlerta(AlertaDto alerta) => NavigationManager.NavigateTo(
        alerta.DocumentoId is { } documentoId
            ? $"/documentos?documentoId={documentoId}"
            : $"/documentos?trabajadorId={alerta.TrabajadorId}&tipoDocumentoId={alerta.TipoDocumentoId}");

    /// <summary>
    /// Una carga que termina después de otra más reciente no escribe nada:
    /// la versión se toma antes del await y se compara al volver.
    /// </summary>
    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var alertas = await Mediator.Send(new ObtenerAlertasQuery());
            if (version != _versionCarga) return;

            _alertas = alertas;
            _pagina = 1;
        }
        catch (Exception)
        {
            if (version != _versionCarga) return;
            _errorCarga = true;
        }
        finally
        {
            if (version == _versionCarga)
                _cargando = false;
        }
    }

    // ------------------------------------------------------------ Reclamación agregada

    private Task ConfirmarFiltroLoteAsync(FiltroLoteDocumental filtro)
    {
        _filtroLote = filtro;
        return CargarLoteAsync();
    }

    /// <summary>
    /// Invalida también la carga que pudiera estar en vuelo: su respuesta es
    /// de un filtro que el usuario acaba de abandonar.
    /// </summary>
    private void CambiarFiltroLote()
    {
        _versionLote++;
        _filtroLote = null;
        _cargandoLote = false;
        _errorLote = false;
        _lotes = [];
        _seleccionPorTitular.Clear();
        _contactosPorTitular.Clear();
    }

    /// <summary>
    /// El filtro y la versión se capturan antes del await. Si mientras tanto
    /// el usuario eligió otro filtro (o se lanzó otra recarga), esta respuesta
    /// ya no es la vigente y se descarta entera: pintarla pondría bajo el
    /// filtro nuevo las tarjetas de uno viejo, y "Enviar" reclamaría esos
    /// documentos.
    /// </summary>
    private async Task CargarLoteAsync()
    {
        if (_filtroLote is not { } filtro) return;

        var version = ++_versionLote;
        _cargandoLote = true;
        _errorLote = false;
        StateHasChanged();

        try
        {
            var lotes = await Mediator.Send(new ObtenerLoteReclamacionPorFiltroQuery(filtro));
            if (version != _versionLote) return;

            _lotes = lotes;
            _seleccionPorTitular.Clear();
            _contactosPorTitular.Clear();
            foreach (var lote in _lotes)
            {
                _seleccionPorTitular[lote.TitularId] = lote.Documentos.Select(d => d.DocumentoId).ToHashSet();
                _contactosPorTitular[lote.TitularId] =
                    (lote.Destinatarios ?? []).Select(d => d.ContactoId).ToHashSet();
            }
        }
        catch (Exception)
        {
            if (version != _versionLote) return;
            _errorLote = true;
        }
        finally
        {
            if (version == _versionLote)
                _cargandoLote = false;
        }
    }

    private void AlternarSeleccion(Guid titularId, Guid documentoId, bool seleccionado)
    {
        var seleccionados = _seleccionPorTitular[titularId];
        if (seleccionado) seleccionados.Add(documentoId);
        else seleccionados.Remove(documentoId);
    }

    private HashSet<Guid> ContactosMarcados(Guid titularId) =>
        _contactosPorTitular.TryGetValue(titularId, out var marcados) ? marcados : [];

    private void AlternarContacto(Guid titularId, Guid contactoId, bool marcado)
    {
        if (!_contactosPorTitular.TryGetValue(titularId, out var marcados))
            _contactosPorTitular[titularId] = marcados = [];

        if (marcado) marcados.Add(contactoId);
        else marcados.Remove(contactoId);
    }

    private void AbrirAltaContacto(LoteReclamacionAgrupadoDto lote)
    {
        _titularAltaContacto = lote.TitularId;
        _tipoAgendaAltaContacto = lote.Ambito == AmbitoAplicacion.Empresa
            ? TipoPropietarioAgenda.Empresa
            : TipoPropietarioAgenda.Cliente;
        _altaContactoVisible = true;
    }

    /// <summary>
    /// Reclamar envía un mensaje a terceros: el botón de la tarjeta no envía,
    /// fija lo que se va a enviar y abre la confirmación. Lo fijado aquí es lo
    /// que sale — ni una recarga del lote ni un cambio de casillas con el
    /// diálogo abierto alteran lo que el diálogo dijo.
    /// </summary>
    private void PedirConfirmacionEnvio(LoteReclamacionAgrupadoDto lote)
    {
        var documentoIds = _seleccionPorTitular[lote.TitularId].ToList();
        var contactoIds = ContactosMarcados(lote.TitularId).ToList();
        if (documentoIds.Count == 0 || contactoIds.Count == 0) return;

        var nombres = (lote.Destinatarios ?? [])
            .Where(d => contactoIds.Contains(d.ContactoId))
            .Select(d => d.Nombre)
            .ToList();

        _envioPendiente = new EnvioPendiente(
            lote.TitularId, lote.TitularNombre, lote.Ambito, documentoIds, contactoIds, nombres);
    }

    private string MensajeConfirmacionEnvio => _envioPendiente is not { } envio
        ? string.Empty
        : $"Se reclamarán {envio.DocumentoIds.Count} documento(s) a {envio.ContactoIds.Count} contacto(s) de la agenda: {string.Join(", ", envio.NombresContactos)}.";

    private void CerrarConfirmacionEnvio(bool visible)
    {
        if (!visible && _enviandoTitularId is null)
            _envioPendiente = null;
    }

    /// <summary>
    /// Si el envío falla, el diálogo sigue abierto para reintentar o cancelar,
    /// y el lote no se recarga: nada cambió.
    /// </summary>
    private async Task ConfirmarEnvioAsync()
    {
        if (_envioPendiente is not { } envio) return;

        _enviandoTitularId = envio.TitularId;
        try
        {
            // Un comando por ámbito: el titular de un lote de Empresa no es un
            // Cliente y su camino de documentos reclamables es otro (ver
            // ObtenerLoteReclamacionPorFiltroQuery.LoteReclamacionAgrupadoDto).
            var resultado = envio.Ambito == AmbitoAplicacion.Empresa
                ? await Mediator.Send(new EnviarReclamacionEmpresaCommand(
                    envio.TitularId, envio.DocumentoIds, ContactoIdsSeleccionados: envio.ContactoIds))
                : await Mediator.Send(new EnviarReclamacionCommand(
                    envio.TitularId, envio.DocumentoIds, CentroId: null, ContactoIdsSeleccionados: envio.ContactoIds));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _envioPendiente = null;
            ToastService.Mostrar($"Reclamación enviada a {envio.TitularNombre}.", TonoToast.Exito);
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos enviar la reclamación. Inténtalo de nuevo.", TonoToast.Error);
            return;
        }
        finally
        {
            _enviandoTitularId = null;
        }

        await CargarLoteAsync();
    }

    private static string FormatearHaceTiempo(DateTime fechaUtc)
    {
        var dias = (int)(DateTime.UtcNow - fechaUtc).TotalDays;
        return dias switch
        {
            <= 0 => "hoy",
            1 => "hace 1 día",
            _ => $"hace {dias} días"
        };
    }
}
