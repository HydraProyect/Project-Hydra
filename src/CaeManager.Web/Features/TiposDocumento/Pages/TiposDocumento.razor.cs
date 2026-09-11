using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Features.TiposDocumento.Pages;

/// <summary>
/// Catálogo global de tipos de documento (Nivel 1), contra su mockup Gen 2
/// «Tipos Documento TALVEG.dc.html». Ruta propia <c>/tipos-documento</c> y
/// panel del hub de Configuración (<see cref="CaeManager.Web.Components.PaginaIntegrableConfiguracionBase"/>).
///
/// <para>
/// <b>Cuatro reglas de carrera, una por cada cosa que viaja:</b>
/// <list type="bullet">
/// <item>la lista (<see cref="_versionCarga"/>): una respuesta que ya no es la
/// de los filtros vigentes se descarta;</item>
/// <item>los selectores en cascada (<see cref="_versionSelectores"/>): las
/// empresas de un cliente que ya no está elegido no se pintan;</item>
/// <item>el formulario (<see cref="_versionDrawer"/>): pulsar «Editar» en dos
/// filas seguidas abre la segunda, no la que responda la última;</item>
/// <item>los cambios en línea (<see cref="_pendientes"/>): un segundo cambio
/// sobre el mismo valor mientras el primero viaja se ignora, y una lista que
/// se leyó antes de que el cambio se confirmara no lo deshace
/// (<see cref="_escriturasEnLinea"/>).</item>
/// </list>
/// </para>
/// </summary>
public partial class TiposDocumento : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    /// <summary>Lo que dura «Guardado»/«No guardado» junto a un valor cambiado en la fila.</summary>
    private static readonly TimeSpan DuracionAvisoCambio = TimeSpan.FromMilliseconds(1200);

    // Literales de siempre de esta pantalla: FlujoCicloDocumentalTests localiza
    // el interruptor de Verificación IA por este title exacto.
    private const string AyudaLecturaIa = "Lectura automática por IA para este tipo de documento, en todos los clientes";
    private const string AyudaDeteccionTrabajadores = "Detecta automáticamente altas y bajas de personal comparando este documento (p. ej. ITA, RNT) contra los trabajadores activos de la empresa";
    private const string AyudaVerificacionIa = "Verifica por IA el tipo, fecha de emisión y firma del documento contra lo introducido al subirlo, con confidence score";

    /// <summary>Los valores que se cambian desde la propia fila, sin abrir el formulario.</summary>
    private enum CampoEnLinea { LecturaIa, DeteccionTrabajadores, VerificacionIa, DocumentoOficial }

    private enum DesenlaceCambio { Guardado, NoGuardado }

    private readonly record struct ClaveCambio(Guid TipoId, CampoEnLinea Campo);

    private int _tamanoPagina = 20;

    private IReadOnlyList<TipoDocumentoListaDto> _tipos = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private bool _listaPintada;
    private int _pagina = 1;
    private int _versionCarga;

    private IReadOnlyList<ClienteSelectorDto> _clientesFiltroDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasFiltroDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosFiltroDisponibles = [];
    private string _clienteFiltroId = string.Empty;
    private string _empresaFiltroId = string.Empty;
    private string _centroFiltroId = string.Empty;
    private string _terminoBusqueda = string.Empty;
    private int _versionSelectores;

    private readonly HashSet<ClaveCambio> _pendientes = [];
    private readonly Dictionary<ClaveCambio, DesenlaceCambio> _desenlaces = [];
    private readonly Dictionary<ClaveCambio, int> _sellosDesenlace = [];
    private readonly Dictionary<ClaveCambio, int> _generaciones = [];
    private int _ultimoSello;
    private int _escriturasEnLinea;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];

    private bool _drawerVisible;
    private int _versionDrawer;
    private Guid? _editandoId;
    private string _nombre = string.Empty;
    private AmbitoAplicacion _ambito = AmbitoAplicacion.Trabajador;
    private bool _aplicaVencimientoAutomatico;
    private RequisitoDocumental _requerido;
    private RequisitoDocumental _requeridoOriginal;
    private NaturalezaJuridica _naturaleza;
    private string _vigenciaMeses = string.Empty;
    private string _orden = "0";
    private string _notas = string.Empty;
    private string _descripcion = string.Empty;
    private string _criteriosValidacion = string.Empty;
    private string _seSolicitaA = string.Empty;
    private string _observaciones = string.Empty;
    private HashSet<Guid> _centroIdsSeleccionados = [];
    private HashSet<Guid> _centroIdsOriginales = [];
    private HashSet<Guid> _centroIdsExcluidosOriginales = [];
    private string _aliasNuevo = string.Empty;
    private List<string> _aliasesSeleccionados = [];
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmacionVisible;
    private string _mensajeConfirmacion = string.Empty;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_tipos.Count / (double)_tamanoPagina));
    private IReadOnlyList<TipoDocumentoListaDto> TiposDePagina => _tipos.Skip((_pagina - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();

    /// <summary>Dentro del hub se queda en el hub; fuera, la ruta propia del selector.</summary>
    private string RutaLecturaIaPorCliente => IntegradaEnConfiguracion ? "/configuracion/ia" : "/configuracion/lectura-ia";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _clientesFiltroDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        }
        catch (Exception)
        {
            // El filtro por cliente se queda sin opciones; la lista de tipos no depende de él.
            ToastService.Mostrar("No pudimos cargar los clientes del filtro. La lista de tipos sí se carga.", TonoToast.Error);
        }

        await CargarAsync();
    }

    public void Dispose()
    {
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    // ---------------------------------------------------------------- lista

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

    private Task ReintentarAsync()
    {
        _listaPintada = false;
        return CargarAsync();
    }

    /// <summary>
    /// Carga la lista con los filtros vigentes. La versión se captura antes del
    /// <c>await</c>: si entretanto se pidió otra, esta respuesta ya no es la de
    /// los filtros que se ven y se descarta.
    ///
    /// <para>
    /// Si mientras viajaba se confirmó o se deshizo un cambio en línea, la
    /// lista pudo leerse ANTES de ese cambio y lo desharía en pantalla: se
    /// vuelve a pedir en vez de pintarla.
    /// </para>
    /// </summary>
    private async Task CargarAsync()
    {
        while (true)
        {
            var version = ++_versionCarga;
            var escrituras = _escriturasEnLinea;
            var consulta = ConsultaVigente();

            _cargando = true;
            _errorCarga = false;
            StateHasChanged();

            IReadOnlyList<TipoDocumentoListaDto> tipos;
            try
            {
                tipos = await Mediator.Send(consulta);
            }
            catch (Exception)
            {
                if (version == _versionCarga)
                {
                    _errorCarga = true;
                    _listaPintada = false;
                    _cargando = false;
                }

                return;
            }

            if (version != _versionCarga)
                return;

            if (escrituras != _escriturasEnLinea)
                continue;

            _tipos = tipos;
            _pagina = 1;
            _listaPintada = true;
            _cargando = false;
            return;
        }
    }

    private ObtenerTiposDocumentoQuery ConsultaVigente() => new(
        Guid.TryParse(_clienteFiltroId, out var clienteId) ? clienteId : null,
        Guid.TryParse(_empresaFiltroId, out var empresaId) ? empresaId : null,
        Guid.TryParse(_centroFiltroId, out var centroId) ? centroId : null,
        Texto: string.IsNullOrWhiteSpace(_terminoBusqueda) ? null : _terminoBusqueda);

    private async Task CambiarClienteFiltroAsync(string clienteId)
    {
        var version = ++_versionSelectores;
        _clienteFiltroId = clienteId;
        _empresaFiltroId = string.Empty;
        _centroFiltroId = string.Empty;
        _empresasFiltroDisponibles = [];
        _centrosFiltroDisponibles = [];

        if (Guid.TryParse(clienteId, out var id))
        {
            IReadOnlyList<EmpresaSelectorDto> empresas;
            try
            {
                empresas = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(id));
            }
            catch (Exception)
            {
                empresas = [];
                if (version == _versionSelectores)
                    ToastService.Mostrar("No pudimos cargar las empresas de este cliente.", TonoToast.Error);
            }

            // Otro cambio de filtro ya tomó el relevo, y es él quien recarga.
            if (version != _versionSelectores)
                return;

            _empresasFiltroDisponibles = empresas;
        }

        await CargarAsync();
    }

    private async Task CambiarEmpresaFiltroAsync(string empresaId)
    {
        var version = ++_versionSelectores;
        _empresaFiltroId = empresaId;
        _centroFiltroId = string.Empty;
        _centrosFiltroDisponibles = [];

        if (Guid.TryParse(empresaId, out var id) && Guid.TryParse(_clienteFiltroId, out var clienteId))
        {
            IReadOnlyList<CentroSelectorDto> centros;
            try
            {
                centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(clienteId, id));
            }
            catch (Exception)
            {
                centros = [];
                if (version == _versionSelectores)
                    ToastService.Mostrar("No pudimos cargar los centros de esta empresa.", TonoToast.Error);
            }

            if (version != _versionSelectores)
                return;

            _centrosFiltroDisponibles = centros;
        }

        await CargarAsync();
    }

    private Task CambiarCentroFiltroAsync(string centroId)
    {
        _centroFiltroId = centroId;
        return CargarAsync();
    }

    // Busca por Nombre o por cualquiera de los alias (TC2, CIF...) — la razón
    // de ser del campo de alias: antes de que existiera, la única forma de
    // encontrar "TC2" era que estuviera escrito dentro del nombre.
    private Task BuscarAsync(string texto)
    {
        _terminoBusqueda = texto;
        return CargarAsync();
    }

    /// <summary>
    /// Los cuatro filtros de la barra. Separa "aún no hay tipos" de "ninguno
    /// con estos filtros": ofrecer "+ Nuevo tipo" a quien acaba de filtrar lo
    /// manda a duplicar un tipo que ya existe.
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_terminoBusqueda) || !string.IsNullOrWhiteSpace(_clienteFiltroId)
        || !string.IsNullOrWhiteSpace(_empresaFiltroId) || !string.IsNullOrWhiteSpace(_centroFiltroId);

    /// <summary>
    /// Quita los cuatro filtros en una sola recarga. Encadenar los manejadores
    /// lanzaría hasta cuatro consultas y las tres primeras devolverían listas
    /// que ya no se van a pintar. Los selectores dependientes se vacían con
    /// ellos —sin cliente, la lista de empresas cargada ya no aplica— y la
    /// versión de la cascada sube, para que unas empresas todavía en vuelo no
    /// vuelvan a rellenarlos.
    /// </summary>
    private Task LimpiarFiltrosAsync()
    {
        _versionSelectores++;
        _terminoBusqueda = string.Empty;
        _clienteFiltroId = string.Empty;
        _empresaFiltroId = string.Empty;
        _centroFiltroId = string.Empty;
        _empresasFiltroDisponibles = [];
        _centrosFiltroDisponibles = [];
        return CargarAsync();
    }

    // ---------------------------------------------------------------- cambios en línea

    private static bool ValorEnLinea(TipoDocumentoListaDto tipo, CampoEnLinea campo) => campo switch
    {
        CampoEnLinea.LecturaIa => tipo.LecturaIaActiva,
        CampoEnLinea.DeteccionTrabajadores => tipo.DeteccionTrabajadoresActiva,
        CampoEnLinea.VerificacionIa => tipo.VerificacionIaActiva,
        _ => throw new ArgumentOutOfRangeException(nameof(campo), campo, "No es un interruptor.")
    };

    private static TipoDocumentoListaDto ConValor(TipoDocumentoListaDto tipo, CampoEnLinea campo, bool activa) => campo switch
    {
        CampoEnLinea.LecturaIa => tipo with { LecturaIaActiva = activa },
        CampoEnLinea.DeteccionTrabajadores => tipo with { DeteccionTrabajadoresActiva = activa },
        CampoEnLinea.VerificacionIa => tipo with { VerificacionIaActiva = activa },
        _ => throw new ArgumentOutOfRangeException(nameof(campo), campo, "No es un interruptor.")
    };

    private static IRequest<Result> ComandoInterruptor(Guid tipoId, CampoEnLinea campo, bool activa) => campo switch
    {
        CampoEnLinea.LecturaIa => new ActualizarLecturaIaGlobalCommand(tipoId, activa),
        CampoEnLinea.DeteccionTrabajadores => new ActualizarDeteccionTrabajadoresGlobalCommand(tipoId, activa),
        CampoEnLinea.VerificacionIa => new ActualizarVerificacionIaGlobalCommand(tipoId, activa),
        _ => throw new ArgumentOutOfRangeException(nameof(campo), campo, "No es un interruptor.")
    };

    private Task AlternarAsync(Guid tipoId, CampoEnLinea campo)
    {
        var clave = new ClaveCambio(tipoId, campo);
        if (_pendientes.Contains(clave))
        {
            // El navegador ya movió la casilla: se recrea con el valor del modelo.
            Resincronizar(clave);
            return Task.CompletedTask;
        }

        var tipo = _tipos.FirstOrDefault(t => t.Id == tipoId);
        if (tipo is null)
            return Task.CompletedTask;

        var activa = !ValorEnLinea(tipo, campo);
        return GuardarEnLineaAsync(tipo, clave,
            t => ConValor(t, campo, activa),
            t => ConValor(t, campo, !activa),
            ComandoInterruptor(tipoId, campo, activa));
    }

    private Task CambiarPerfilDocumentoOficialAsync(Guid tipoId, string? valorSeleccionado)
    {
        var clave = new ClaveCambio(tipoId, CampoEnLinea.DocumentoOficial);
        var tipo = _tipos.FirstOrDefault(t => t.Id == tipoId);
        if (_pendientes.Contains(clave) || tipo is null
            || !Enum.TryParse<PerfilDocumentoOficial>(valorSeleccionado, out var perfil))
        {
            Resincronizar(clave);
            return Task.CompletedTask;
        }

        if (perfil == tipo.PerfilDocumentoOficial)
            return Task.CompletedTask;

        var anterior = tipo.PerfilDocumentoOficial;
        return GuardarEnLineaAsync(tipo, clave,
            t => t with { PerfilDocumentoOficial = perfil },
            t => t with { PerfilDocumentoOficial = anterior },
            new ActualizarPerfilDocumentoOficialGlobalCommand(tipoId, perfil));
    }

    /// <summary>
    /// Cambio optimista: el valor cambia en la fila al instante, la tabla no
    /// se sustituye por el esqueleto y el comando viaja por detrás. Si falla,
    /// se restaura el valor anterior y se dice por qué.
    /// </summary>
    private async Task GuardarEnLineaAsync(
        TipoDocumentoListaDto tipo, ClaveCambio clave,
        Func<TipoDocumentoListaDto, TipoDocumentoListaDto> aplicar,
        Func<TipoDocumentoListaDto, TipoDocumentoListaDto> deshacer,
        IRequest<Result> comando)
    {
        // Sin await entre la comprobación del llamador y esta marca: el
        // segundo evento de un doble clic ya la encuentra puesta.
        _pendientes.Add(clave);
        _desenlaces.Remove(clave);
        Parchear(tipo.Id, aplicar);

        bool guardado;
        string? motivo = null;
        try
        {
            var resultado = await Mediator.Send(comando);
            guardado = resultado.EsExitoso;
            if (!guardado)
                motivo = resultado.Error.Mensaje;
        }
        catch (Exception)
        {
            guardado = false;
        }
        finally
        {
            _pendientes.Remove(clave);
            _escriturasEnLinea++;
        }

        if (guardado)
        {
            // Otra vez: una lista que llegó mientras el comando viajaba pudo
            // traer el valor de antes.
            Parchear(tipo.Id, aplicar);
            MostrarDesenlace(clave, DesenlaceCambio.Guardado);
            return;
        }

        Parchear(tipo.Id, deshacer);
        Resincronizar(clave);
        MostrarDesenlace(clave, DesenlaceCambio.NoGuardado);
        ToastService.Mostrar(
            motivo is null
                ? $"No se pudo guardar el cambio en «{tipo.Nombre}». Se ha restaurado el valor anterior."
                : $"No se pudo guardar el cambio en «{tipo.Nombre}». {motivo} Se ha restaurado el valor anterior.",
            TonoToast.Error);
    }

    private void Parchear(Guid tipoId, Func<TipoDocumentoListaDto, TipoDocumentoListaDto> cambio) =>
        _tipos = _tipos.Select(t => t.Id == tipoId ? cambio(t) : t).ToList();

    /// <summary>
    /// Clave de render del control: al subirla, Blazor recrea el elemento con
    /// el valor del modelo. Hace falta cuando el navegador ya movió el control
    /// y el modelo no cambió —cambio ignorado por estar otro en vuelo— porque
    /// Blazor solo reescribe <c>checked</c>/<c>value</c> si su valor de render cambia.
    /// </summary>
    private int GeneracionDe(ClaveCambio clave) => _generaciones.GetValueOrDefault(clave);

    private void Resincronizar(ClaveCambio clave) => _generaciones[clave] = GeneracionDe(clave) + 1;

    private void MostrarDesenlace(ClaveCambio clave, DesenlaceCambio desenlace)
    {
        if (_desechado)
            return;

        _desenlaces[clave] = desenlace;
        var sello = ++_ultimoSello;
        _sellosDesenlace[clave] = sello;
        _ = RetirarDesenlaceAsync(clave, sello, _ciclo.Token);
    }

    private async Task RetirarDesenlaceAsync(ClaveCambio clave, int sello, CancellationToken token)
    {
        try
        {
            await Task.Delay(DuracionAvisoCambio, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            // Un cambio posterior sobre el mismo valor trae su propio aviso.
            if (_desechado || _sellosDesenlace.GetValueOrDefault(clave) != sello)
                return;

            _desenlaces.Remove(clave);
            StateHasChanged();
        });
    }

    /// <summary>Texto de estado mientras viaja o recién llegado; <c>null</c> cuando no hay nada que contar.</summary>
    private string? TextoAvisoCambio(ClaveCambio clave)
    {
        if (_pendientes.Contains(clave))
            return "Guardando…";

        return _desenlaces.TryGetValue(clave, out var desenlace)
            ? desenlace == DesenlaceCambio.Guardado ? "Guardado ✓" : "No guardado"
            : null;
    }

    private string ClaseAvisoCambio(ClaveCambio clave) =>
        !_pendientes.Contains(clave) && _desenlaces.TryGetValue(clave, out var desenlace)
            ? desenlace == DesenlaceCambio.Guardado ? "aviso-cambio aviso-cambio-guardado" : "aviso-cambio aviso-cambio-fallido"
            : "aviso-cambio";

    // ---------------------------------------------------------------- textos

    private static string TextoAmbito(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Trabajador => "Trabajador",
        AmbitoAplicacion.Cliente => "Cliente",
        AmbitoAplicacion.Empresa => "Empresa",
        AmbitoAplicacion.Vehiculo => "Vehículo",
        AmbitoAplicacion.Proyecto => "Proyecto",
        _ => ambito.ToString()
    };

    private static string TextoVigencia(int? meses) => meses switch
    {
        null => "—",
        1 => "1 mes",
        _ => $"{meses} meses"
    };

    private static string TextoRequerido(RequisitoDocumental requerido) => requerido switch
    {
        RequisitoDocumental.No => "No se pide",
        RequisitoDocumental.Si => "Sí, siempre",
        RequisitoDocumental.Condicional => "Solo si aplica",
        _ => "Requisito desconocido"
    };

    private static string TextoExigencia(RequisitoDocumental requerido, NaturalezaJuridica naturaleza) =>
        requerido == RequisitoDocumental.No ? "Opcional" : RequisitoDocumentalUi.Texto(requerido, naturaleza);

    private static TonoBadge TonoExigencia(RequisitoDocumental requerido, NaturalezaJuridica naturaleza) =>
        requerido == RequisitoDocumental.No ? TonoBadge.Neutro : RequisitoDocumentalUi.Tono(naturaleza);

    /// <summary>
    /// Los dos ejes tal como se configuraron, y nada más. El mockup añadía
    /// «Una norma lo exige, sin condiciones»: la pantalla no sabe qué exige
    /// ninguna norma, solo lo que alguien marcó aquí.
    /// </summary>
    private static string AyudaExigencia(RequisitoDocumental requerido, NaturalezaJuridica naturaleza) =>
        $"¿Se pide? {TextoRequerido(requerido)} · ¿Con qué autoridad? {RequisitoDocumentalUi.TextoNaturaleza(naturaleza)}. "
        + "Es el valor general: un centro puede tener su propia configuración.";

    private string PistaOrden => HayFiltrosActivos
        ? "Propuesto a partir de la lista filtrada: comprueba que no coincida con el de otro tipo."
        : "Propuesto: el siguiente libre.";

    /// <summary>
    /// Lo que de verdad pasa con los centros sin marcar: siguen el valor
    /// general de «¿Se pide?» (ResolucionTipoDocumentoCentro.Aplica), así que
    /// «vacío = aplica en todos» solo es cierto con «Sí, siempre».
    /// </summary>
    private string ResumenCentros
    {
        get
        {
            var marcados = _centrosDisponibles.Count(c => _centroIdsSeleccionados.Contains(c.Id));
            var total = _centrosDisponibles.Count;
            var general = TextoRequerido(_requerido);
            var sePide = _requerido == RequisitoDocumental.Si;

            if (marcados == 0)
            {
                return sePide
                    ? $"Sin marcar ninguno, cada centro sigue el valor general («{general}») y lo pide, salvo los que lo tengan excluido en sus propios requisitos."
                    : $"Sin marcar ninguno, cada centro sigue el valor general («{general}») y no lo pide por defecto.";
            }

            return sePide
                ? $"Marcado en {marcados} de {total} centros. Con el valor general «{general}», el resto también lo pide salvo los que lo tengan excluido."
                : $"Se pide en los {marcados} centros marcados (de {total}). El resto sigue el valor general («{general}») y no lo pide por defecto.";
        }
    }

    // ---------------------------------------------------------------- formulario

    private async Task AbrirCrearAsync()
    {
        var version = ++_versionDrawer;
        IReadOnlyList<CentroSelectorDto> centros;
        try
        {
            centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        }
        catch (Exception)
        {
            if (version == _versionDrawer)
                ToastService.Mostrar("No pudimos abrir el formulario. Intenta nuevamente en unos segundos.", TonoToast.Error);
            return;
        }

        if (version != _versionDrawer)
            return;

        _centrosDisponibles = centros;
        _editandoId = null;
        _nombre = string.Empty;
        _ambito = AmbitoAplicacion.Trabajador;
        _aplicaVencimientoAutomatico = false;
        _requerido = RequisitoDocumental.No;
        _requeridoOriginal = RequisitoDocumental.No;
        _naturaleza = NaturalezaJuridica.RequisitoCliente;
        _vigenciaMeses = string.Empty;
        _orden = (_tipos.Count > 0 ? _tipos.Max(t => t.Orden) + 1 : 1).ToString();
        _notas = string.Empty;
        _descripcion = string.Empty;
        _criteriosValidacion = string.Empty;
        _seSolicitaA = string.Empty;
        _observaciones = string.Empty;
        _centroIdsSeleccionados = [];
        _centroIdsOriginales = [];
        _centroIdsExcluidosOriginales = [];
        _aliasNuevo = string.Empty;
        _aliasesSeleccionados = [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var version = ++_versionDrawer;
        IReadOnlyList<CentroSelectorDto> centros;
        TipoDocumentoDetalleDto? tipo;
        try
        {
            centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
            if (version != _versionDrawer)
                return;

            tipo = await Mediator.Send(new ObtenerTipoDocumentoPorIdQuery(id));
        }
        catch (Exception)
        {
            if (version == _versionDrawer)
                ToastService.Mostrar("No pudimos abrir el formulario. Intenta nuevamente en unos segundos.", TonoToast.Error);
            return;
        }

        // Se pulsó «Editar» en otra fila mientras esta respondía.
        if (version != _versionDrawer)
            return;

        if (tipo is null)
        {
            ToastService.Mostrar("No encontramos este tipo de documento.", TonoToast.Error);
            await CargarAsync();
            return;
        }

        _centrosDisponibles = centros;
        _editandoId = tipo.Id;
        _nombre = tipo.Nombre;
        _ambito = tipo.AmbitoAplicacion;
        _aplicaVencimientoAutomatico = tipo.AplicaVencimientoAutomatico;
        _requerido = tipo.Requerido;
        _requeridoOriginal = tipo.Requerido;
        _naturaleza = tipo.Naturaleza;
        _vigenciaMeses = tipo.VigenciaMeses?.ToString() ?? string.Empty;
        _orden = tipo.Orden.ToString();
        _notas = tipo.Notas ?? string.Empty;
        _descripcion = tipo.Descripcion ?? string.Empty;
        _criteriosValidacion = tipo.CriteriosValidacion ?? string.Empty;
        _seSolicitaA = tipo.SeSolicitaA ?? string.Empty;
        _observaciones = tipo.Observaciones ?? string.Empty;
        // Incluye los centros que el selector no enseña (fuera del alcance de
        // quien edita): se reenvían tal cual, porque el comando borra por
        // ausencia y lo que no se pudo desmarcar no puede leerse como quitado.
        _centroIdsSeleccionados = tipo.CentroIds.ToHashSet();
        _centroIdsOriginales = tipo.CentroIds.ToHashSet();
        _centroIdsExcluidosOriginales = tipo.CentroIdsExcluidos.ToHashSet();
        _aliasNuevo = string.Empty;
        _aliasesSeleccionados = tipo.Aliases.ToList();
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlternarCentro(Guid centroId, bool seleccionado)
    {
        if (seleccionado)
            _centroIdsSeleccionados.Add(centroId);
        else
            _centroIdsSeleccionados.Remove(centroId);
    }

    private void AgregarAlias()
    {
        var alias = _aliasNuevo.Trim();
        if (alias.Length == 0)
            return;

        if (!_aliasesSeleccionados.Contains(alias, StringComparer.OrdinalIgnoreCase))
            _aliasesSeleccionados.Add(alias);

        _aliasNuevo = string.Empty;
    }

    private void QuitarAlias(string alias) => _aliasesSeleccionados.Remove(alias);

    private Task ManejarTeclaAliasAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            AgregarAlias();

        return Task.CompletedTask;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Si guardar cambia lo que se pide en algún centro, antes se confirma
    /// diciendo ese efecto; si no cambia en ninguno, se guarda sin preguntar.
    /// El cálculo está en <see cref="CalcularCambio"/>.
    /// </summary>
    private async Task GuardarAsync()
    {
        if (_guardando)
            return;

        var efectos = EfectosSobreLoQueSePide();
        if (efectos.Count > 0)
        {
            _mensajeConfirmacion = string.Join(" ", efectos);
            _confirmacionVisible = true;
            return;
        }

        await EnviarAsync();
    }

    private async Task ConfirmarGuardadoAsync()
    {
        await EnviarAsync();
        // Guardado o no, el diálogo ya cumplió: un error se lee en el formulario.
        _confirmacionVisible = false;
    }

    /// <summary>
    /// Clave con la que se evalúa en memoria un tipo que todavía no existe:
    /// solo indexa el par (tipo, centro) y no viaja en ningún comando.
    /// </summary>
    private static readonly Guid TipoAunSinCrear = Guid.Parse("7a1c0000-0000-0000-0000-000000000001");

    /// <summary>
    /// «Cualquier centro sin fila propia»: <see cref="TipoDocumentoCentro"/>
    /// rechaza <see cref="Guid.Empty"/> como centro, así que nunca coincide con una fila.
    /// </summary>
    private static readonly Guid CentroSinFilaPropia = Guid.Empty;

    /// <summary>Lo que cambia en lo que se pide al guardar.</summary>
    /// <param name="Empiezan">Centros con fila antes o después que no lo pedían y pasan a pedirlo.</param>
    /// <param name="Dejan">Centros con fila antes o después que lo pedían y dejan de pedirlo.</param>
    /// <param name="RestoAntes">Si lo pedía un centro sin fila propia, antes de guardar.</param>
    /// <param name="RestoDespues">Si lo pide un centro sin fila propia, después de guardar.</param>
    private sealed record CambioEnLoQueSePide(IReadOnlyList<Guid> Empiezan, IReadOnlyList<Guid> Dejan, bool RestoAntes, bool RestoDespues)
    {
        public bool HayCambio => Empiezan.Count > 0 || Dejan.Count > 0 || RestoAntes != RestoDespues;
    }

    /// <summary>
    /// Qué centros cambian al guardar, evaluados con la misma regla con la que
    /// Application decide si un tipo aplica a un centro
    /// (<see cref="ResolucionTipoDocumentoCentro.Aplica"/>, reutilizada, no
    /// copiada): si el par tiene fila <see cref="TipoDocumentoCentro"/>, manda
    /// su <c>Incluido</c>; si no, el valor general, «¿Se pide?» == «Sí, siempre».
    ///
    /// <para>
    /// El selector solo marca/desmarca filas Incluido=true: es lo único que
    /// <c>CrearTipoDocumentoCommand</c> y <c>EditarTipoDocumentoCommand</c> crean o
    /// borran por ausencia. Pero <c>EditarTipoDocumentoCommand</c> SÍ toca una fila
    /// Incluido=false cuando se marca aquí su centro — la convierte a Incluido=true
    /// en vez de duplicarla (índice único por par) — así que <paramref name="excluidosAntes"/>
    /// entra en el cálculo: un centro excluido que se marca pasa de no pedirlo (pase
    /// lo que pase el valor general, la fila explícita manda) a pedirlo. Un centro
    /// excluido que NO se marca se deja tal cual, sin entrar en el cambio devuelto.
    /// </para>
    /// </summary>
    private static CambioEnLoQueSePide CalcularCambio(
        Guid tipoId, IReadOnlySet<Guid> marcadosAntes, IReadOnlySet<Guid> excluidosAntes, bool generalAntes,
        IReadOnlySet<Guid> marcadosDespues, bool generalDespues)
    {
        // Marcar un centro excluido lo convierte a Incluido=true (EditarTipoDocumentoCommand);
        // uno que sigue sin marcarse conserva su exclusión intacta.
        var excluidosDespues = excluidosAntes.Except(marcadosDespues).ToHashSet();
        var filasAntes = FilasConEstado(tipoId, marcadosAntes, excluidosAntes);
        var filasDespues = FilasConEstado(tipoId, marcadosDespues, excluidosDespues);

        bool Antes(Guid centroId) => ResolucionTipoDocumentoCentro.Aplica(filasAntes, tipoId, centroId, generalAntes);
        bool Despues(Guid centroId) => ResolucionTipoDocumentoCentro.Aplica(filasDespues, tipoId, centroId, generalDespues);

        var conFila = marcadosAntes.Union(marcadosDespues).Union(excluidosAntes).ToList();
        return new CambioEnLoQueSePide(
            Empiezan: conFila.Where(c => !Antes(c) && Despues(c)).ToList(),
            Dejan: conFila.Where(c => Antes(c) && !Despues(c)).ToList(),
            RestoAntes: Antes(CentroSinFilaPropia),
            RestoDespues: Despues(CentroSinFilaPropia));
    }

    private static Dictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro> FilasConEstado(
        Guid tipoId, IEnumerable<Guid> incluidos, IEnumerable<Guid> excluidos)
    {
        var filas = incluidos.ToDictionary(centroId => (tipoId, centroId), centroId => new TipoDocumentoCentro(tipoId, centroId));
        foreach (var centroId in excluidos)
            filas[(tipoId, centroId)] = new TipoDocumentoCentro(tipoId, centroId, incluido: false);

        return filas;
    }

    private List<string> EfectosSobreLoQueSePide()
    {
        var creando = _editandoId is null;
        var cambio = CalcularCambio(
            _editandoId ?? TipoAunSinCrear,
            // Antes de crearlo el tipo no existe: no lo pide ningún centro, ni tiene exclusiones.
            marcadosAntes: creando ? new HashSet<Guid>() : _centroIdsOriginales,
            excluidosAntes: creando ? new HashSet<Guid>() : _centroIdsExcluidosOriginales,
            generalAntes: !creando && _requeridoOriginal == RequisitoDocumental.Si,
            marcadosDespues: CentroIdsQueSeEnvian().ToHashSet(),
            generalDespues: _requerido == RequisitoDocumental.Si);

        var efectos = new List<string>();
        if (!cambio.HayCambio)
            return efectos;

        var nombre = string.IsNullOrWhiteSpace(_nombre) ? "Este tipo de documento" : $"«{_nombre.Trim()}»";

        if (!cambio.RestoAntes && cambio.RestoDespues)
        {
            // Un tipo nuevo no tiene fila en ningún centro: con «Sí, siempre»
            // lo piden todos, los marcados incluidos, y no hay nada más que contar.
            if (creando)
            {
                efectos.Add($"{nombre} se pedirá en todos los centros, porque «¿Se pide?» es «Sí, siempre».");
                return efectos;
            }

            efectos.Add($"{nombre} pasará a pedirse en todos los centros que no tengan su propia configuración para este tipo.");
        }
        else if (cambio.RestoAntes && !cambio.RestoDespues)
        {
            efectos.Add($"{nombre} dejará de pedirse por defecto: los centros que no tengan su propia configuración para este tipo ya no lo pedirán.");
        }

        if (cambio.Empiezan.Count > 0)
            efectos.Add($"{nombre} se pedirá en {CuentaCentros(cambio.Empiezan.Count, "marcado")}{ListaNombresCentros(cambio.Empiezan)}.");

        if (cambio.Dejan.Count > 0)
        {
            var siguen = cambio.Dejan.Count == 1 ? "que pasa" : "que pasan";
            efectos.Add($"{nombre} dejará de pedirse en {CuentaCentros(cambio.Dejan.Count, "desmarcado")}{ListaNombresCentros(cambio.Dejan)}, "
                + $"{siguen} a seguir el valor general («{TextoRequerido(_requerido)}»).");
        }

        return efectos;
    }

    private static string CuentaCentros(int cuantos, string participio) =>
        cuantos == 1 ? $"1 centro {participio}" : $"{cuantos} centros {participio}s";

    /// <summary>
    /// «: Planta Zaragoza, Nave logística Tudela», en el orden del selector.
    /// Si alguno no está en el selector se omiten los nombres: la cuenta ya
    /// es exacta y una lista incompleta diría menos centros de los que son.
    /// </summary>
    private string ListaNombresCentros(IReadOnlyCollection<Guid> centroIds)
    {
        var nombres = _centrosDisponibles.Where(c => centroIds.Contains(c.Id)).Select(c => c.Nombre).ToList();
        return nombres.Count == centroIds.Count ? $": {string.Join(", ", nombres)}" : string.Empty;
    }

    /// <summary>
    /// Los centros que viajan en el comando, y con los que se calcula el
    /// efecto. Al crear, solo con ámbito Trabajador: si se marcaron y luego
    /// se cambió el ámbito, no viajan. Al editar, toda la selección, incluidos
    /// los centros que el selector no enseña (fuera del alcance de quien
    /// edita): <c>EditarTipoDocumentoCommand</c> borra por ausencia, y lo que
    /// no se pudo desmarcar no puede leerse como quitado.
    /// </summary>
    private List<Guid> CentroIdsQueSeEnvian() =>
        _editandoId is null && _ambito != AmbitoAplicacion.Trabajador ? [] : _centroIdsSeleccionados.ToList();

    private async Task EnviarAsync()
    {
        // Guarda de doble clic: se levanta antes del primer await.
        if (_guardando)
            return;

        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var vigenciaMeses = int.TryParse(_vigenciaMeses, out var v) ? v : (int?)null;
            var orden = int.TryParse(_orden, out var o) ? o : 0;
            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            var descripcion = string.IsNullOrWhiteSpace(_descripcion) ? null : _descripcion;
            var criteriosValidacion = string.IsNullOrWhiteSpace(_criteriosValidacion) ? null : _criteriosValidacion;
            var seSolicitaA = string.IsNullOrWhiteSpace(_seSolicitaA) ? null : _seSolicitaA;
            var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;

            string? mensajeError;

            if (_editandoId is null)
            {
                var resultado = await Mediator.Send(
                    new CrearTipoDocumentoCommand(
                        _nombre, vigenciaMeses, _aplicaVencimientoAutomatico, orden, _ambito, _requerido, _naturaleza, notas,
                        descripcion, criteriosValidacion, seSolicitaA, observaciones, CentroIdsQueSeEnvian(), _aliasesSeleccionados));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new EditarTipoDocumentoCommand(
                        _editandoId.Value, _nombre, vigenciaMeses, _aplicaVencimientoAutomatico, orden, _requerido, _naturaleza, notas,
                        descripcion, criteriosValidacion, seSolicitaA, observaciones, CentroIdsQueSeEnvian(), _aliasesSeleccionados));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Tipo de documento creado correctamente." : "Tipo de documento actualizado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
            _mensajeErrorFormulario = "Revisa los campos marcados.";
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);
}
