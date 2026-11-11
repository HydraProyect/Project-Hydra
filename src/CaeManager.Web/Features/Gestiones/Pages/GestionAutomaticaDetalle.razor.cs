using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Gestiones.Components;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Gestiones.Pages;

/// <summary>Ver <c>GestionAutomaticaDetalle.razor</c>.</summary>
public partial class GestionAutomaticaDetalle : CaeManager.Web.Components.PaginaInteractiva
{
    internal const string OpcionSubido = "subido";
    internal const string OpcionValidado = "validado";
    internal const string OpcionRechazado = "rechazado";
    internal const string OpcionNoPide = "nopide";

    // Orden y textos de la maqueta (OPC): estado de la acreditación que deja cada opción.
    private static readonly (string Clave, EstadoAcreditacion Estado, string Titulo, string Descripcion)[] Opciones =
    [
        (OpcionSubido, EstadoAcreditacion.Subida, "GpOpcionSubidoTitulo", "GpOpcionSubidoDescripcion"),
        (OpcionValidado, EstadoAcreditacion.Aceptada, "GpOpcionValidadoTitulo", "GpOpcionValidadoDescripcion"),
        (OpcionRechazado, EstadoAcreditacion.Rechazada, "GpOpcionRechazadoTitulo", "GpOpcionRechazadoDescripcion"),
        (OpcionNoPide, EstadoAcreditacion.NoRequerida, "GpOpcionNoPideTitulo", "GpOpcionNoPideDescripcion")
    ];

    // Las cinco causas de la maqueta, sobre las siete del dominio.
    private static readonly (string Valor, string Clave)[] Causas =
    [
        (nameof(CausaRechazoAcreditacion.Ilegible), "GpCausaIlegible"),
        (nameof(CausaRechazoAcreditacion.CaducadoAlPresentar), "GpCausaCaducado"),
        (nameof(CausaRechazoAcreditacion.DocumentoEquivocado), "GpCausaNoCorresponde"),
        (nameof(CausaRechazoAcreditacion.DatosErroneos), "GpCausaDatos"),
        (nameof(CausaRechazoAcreditacion.Otro), "GpCausaOtra")
    ];

    private GestionAutomaticaDetalleDto? _detalle;
    private Guid? _abiertoId;
    private bool _cargando = true;
    private bool _error;
    private bool _menuAbierto;
    private bool _soloPorHacer = true;
    private bool _puedeEscribir;
    private bool _reabierta;
    private string _textoReabierta = string.Empty;

    private string? _opcion;
    private DateOnly? _hasta;
    private bool _noSe;
    private string _causa = string.Empty;
    private string _motivo = string.Empty;
    private bool _confirmaNoPide;
    private bool _guardando;
    private string? _errorGuardado;

    // «Datos a mano»: pestaña elegida (null = la del sujeto del paso) y el editor de la ficha del Trabajador.
    private string? _pestana;
    private bool _editorTrabajadorVisible;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;

    /// <summary>
    /// El aviso «Paso guardado» con Deshacer es el toast del sistema de diseño: dura 8 s, se pausa con el puntero o el
    /// foco y sobrevive a «Siguiente Gestión», que es justo cuando Deshacer hace falta.
    /// </summary>
    [Inject] private ToastService Toasts { get; set; } = default!;

    [Parameter] public Guid Id { get; set; }

    /// <summary>Abre la Gestión con este paso (acreditación) abierto: el enlace «En Gestión» de Centro 360.</summary>
    [SupplyParameterFromQuery(Name = "paso")] public Guid? PasoSolicitado { get; set; }

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private Guid _cargada;
    private Guid? _pasoCargado;

    protected override async Task OnParametersSetAsync()
    {
        if (_cargada == Id)
        {
            // La misma Gestión con otro «?paso=» (Deshacer desde otra Gestión, enlace «En Gestión»): solo cambia el paso.
            if (PasoSolicitado != _pasoCargado)
            {
                _pasoCargado = PasoSolicitado;
                if (PasoSolicitado is { } paso) Abrir(paso);
            }

            return;
        }

        _cargada = Id;
        _pasoCargado = PasoSolicitado;

        // Otra Gestión en la misma instancia (Siguiente Gestión, enlace a la nueva): nada de la anterior sobrevive.
        _detalle = null;
        _cargando = true;
        _error = false;
        _menuAbierto = false;
        _soloPorHacer = true;
        _reabierta = false;
        _textoReabierta = string.Empty;
        Abrir(PasoSolicitado ?? Guid.Empty);
        _abiertoId = PasoSolicitado;
        await CargarAsync();
    }

    private bool PasoRechazado => PasoAbierto?.Estado == EstadoAcreditacion.Rechazada;

    private IReadOnlyList<PasoGestionAutomaticaDto> Pasos => _detalle?.Pasos ?? [];

    private int PorHacer => Pasos.Count(p => !p.Hecho);

    private IEnumerable<IGrouping<string, PasoGestionAutomaticaDto>> Grupos =>
        Pasos.Where(p => !_soloPorHacer || !p.Hecho).GroupBy(p => p.Plataforma);

    /// <summary>Paso abierto: el elegido; si no, el primero rechazado, el primero por hacer o el primero (§ 5.1.3).</summary>
    private PasoGestionAutomaticaDto? PasoAbierto =>
        Pasos.FirstOrDefault(p => p.AcreditacionId == _abiertoId)
        ?? Pasos.FirstOrDefault(p => p.Estado == EstadoAcreditacion.Rechazada)
        ?? Pasos.FirstOrDefault(p => !p.Hecho)
        ?? Pasos.FirstOrDefault();

    private int IndiceAbierto => PasoAbierto is { } p ? Pasos.ToList().FindIndex(x => x.AcreditacionId == p.AcreditacionId) : -1;

    private string TituloGestion => _detalle is { } d
        ? Textos[d.Resumen.Origen == OrigenGestionAutomatica.RenovacionDocumento ? "AutoTituloRenovacion" : "AutoTituloAlta", d.Resumen.TipoDocumentoNombre, d.Resumen.TitularNombre]
        : Textos["AutoTituloPagina"];

    private string TextoOrigen(GestionAutomaticaResumenDto g)
    {
        var cuando = g.CreadoEnUtc.EnHoraPeninsular();
        return Textos[g.Origen == OrigenGestionAutomatica.RenovacionDocumento ? "GpOrigenRenovacion" : "GpOrigenAlta", cuando.ToString("dd/MM/yyyy"), cuando.ToString("HH:mm")];
    }

    private string TextoAvance(AvanceGestionAutomatica a) => Textos[
        a.Validados == 1 ? (a.PendientesDeValidar == 1 ? "GpAvanceUnoUno" : "GpAvanceUnoVarios") : (a.PendientesDeValidar == 1 ? "GpAvanceVariosUno" : "GpAvanceVariosVarios"),
        a.Hechos, a.Pasos, a.Validados, a.PendientesDeValidar];

    private int Rechazados => _detalle?.Pasos.Count(p => p.Estado == EstadoAcreditacion.Rechazada) ?? 0;

    private (int Hechos, int Total) AvanceDePlataforma(string plataforma)
    {
        var delGrupo = _detalle?.Pasos.Where(p => p.Plataforma == plataforma).ToList() ?? [];
        return (delGrupo.Count(p => p.Hecho), delGrupo.Count);
    }

    private string TextoPasoCorto(PasoGestionAutomaticaDto p) =>
        p.Estado == EstadoAcreditacion.Aceptada && p.FechaVencimientoEnPlataforma is { } hasta
            ? Textos["PasoValidadoHasta", hasta.ToString("dd/MM/yyyy")]
            : Textos[PresentacionGestionAutomatica.ClavePasoCorto(p.Estado)];

    private string? DetallePaso(PasoGestionAutomaticaDto p) =>
        p.Estado == EstadoAcreditacion.Aceptada && p.FechaVencimientoEnPlataforma is { } hasta
            ? Textos["PasoDetalleValidadoHasta", hasta.ToString("dd/MM/yyyy")].Value
            : null;

    private string RutaFichaTitular => _detalle?.Documento switch
    {
        { TrabajadorId: { } t } => $"trabajadores/{t}",
        { EmpresaId: { } e } => $"empresas/{e}",
        _ => "gestiones/automaticas"
    };

    /// <summary>El canal del paso con la forma que espera el componente de acceso de Centro 360 (sin credenciales: se piden al pulsar).</summary>
    private static CanalGestionResumenDto CanalDe(PasoGestionAutomaticaDto p) => new(
        p.CanalId, TipoCanalGestion.Plataforma, p.Plataforma, false, null, p.Plataforma, p.UrlAcceso, null, null, p.Notas,
        p.TieneUsuario, Guid.Empty);

    private IEnumerable<(string Clave, string Valor, bool Copiable, string Tono)> Campos(GestionAutomaticaResumenDto resumen)
    {
        var documento = _detalle!.Documento;
        yield return ("GpCampoTipo", resumen.TipoDocumentoNombre, true, "");
        yield return ("GpCampoTitular", resumen.TitularNombre, true, "");
        if (!string.IsNullOrWhiteSpace(documento.IdentificadorTitular))
            yield return (documento.TrabajadorId is not null ? "GpCampoDni" : "GpCampoCif", documento.IdentificadorTitular!, true, "");
        yield return ("GpCampoEmision", documento.FechaEmision.ToString("dd/MM/yyyy"), true, "");
        yield return ("GpCampoVencimiento", documento.FechaVencimiento?.ToString("dd/MM/yyyy") ?? Textos["GpNoCaduca"].Value, documento.FechaVencimiento is not null, "");

        var vence = documento.FechaVencimiento?.ToString("dd/MM/yyyy");
        if (documento.VigenteEnTalveg)
            yield return ("GpCampoEstadoTalveg", vence is null ? Textos["GpVigente"] : Textos["GpVigenteHasta", vence], false, "exito");
        else
            yield return ("GpCampoEstadoTalveg", documento.FechaVencimiento is { } v && v < DiaDeNegocio.Hoy() ? Textos["GpVencidoEl", vence!] : Textos["GpNoVigenteValor"], false, "peligro");
    }

    internal const string PestanaTrabajador = "trabajador";
    internal const string PestanaCentro = "centro";

    /// <summary>
    /// Pestañas de «Datos a mano» (ventana de la Gestión, § 1.1): Trabajador si el Documento es de un Trabajador que llega
    /// en el detalle, y Centro siempre. Empleador y Cliente van en un incremento posterior.
    /// </summary>
    private IEnumerable<string> Pestanas()
    {
        if (_detalle?.Trabajador is not null) yield return PestanaTrabajador;
        yield return PestanaCentro;
    }

    /// <summary>La elegida; si no, la del sujeto del paso (en un paso de documento, su titular: el Trabajador).</summary>
    private string PestanaActual => _pestana is { } elegida && Pestanas().Contains(elegida) ? elegida : Pestanas().First();

    /// <summary>
    /// Los datos de una pestaña, en el orden de la maqueta (datosDe). Valor null = «Falta en TALVEG». El valor ya va con el
    /// formato que piden los portales: fechas dd/mm/aaaa y DNI sin guiones ni espacios. Fecha de baja e inicio de la
    /// asignación solo salen si existen: no son un dato que falte.
    /// </summary>
    private IEnumerable<(string Clave, string? Valor)> DatosDe(string pestana, PasoGestionAutomaticaDto paso)
    {
        if (pestana == PestanaTrabajador && _detalle?.Trabajador is { } t)
        {
            yield return ("GpDatoNombre", t.NombreCompleto);
            yield return ("GpDatoDni", DniParaPortal(t.Dni));
            yield return ("GpDatoNacimiento", Fecha(t.FechaNacimiento));
            yield return ("GpDatoNaf", Texto(t.NumeroAfiliacionSeguridadSocial));
            yield return ("GpDatoNacionalidad", Texto(t.Nacionalidad));
            yield return ("GpDatoPuesto", Texto(t.Puesto));
            yield return ("GpDatoContratacion", Texto(t.TipoVinculo));
            yield return ("GpDatoAltaEmpresa", Fecha(t.FechaAltaEmpresa));
            yield return ("GpDatoRnt", Texto(t.FiguraEnRnt));
            // Sin la política de la nota interna el permiso no llega (null) y no es un dato que falte: ni fila ni «Completar».
            if (t.PermisoTrabajoVisible) yield return ("GpDatoPermiso", Texto(t.TipoPermisoTrabajo));
            if (t.FechaBajaEmpresa is { } baja) yield return ("GpDatoBaja", baja.ToString("dd/MM/yyyy"));
            yield break;
        }

        yield return ("GpDatoCentro", paso.CentroNombre);
        yield return ("GpDatoCodigoCentro", Texto(paso.CentroCodigo));
        yield return ("GpDatoDireccion", Texto(paso.CentroDireccion));
        if (paso.InicioAsignacion is { } inicio) yield return ("GpDatoInicioAsignacion", inicio.ToString("dd/MM/yyyy"));
        yield return ("GpDatoAcceso", paso.Plataforma);
    }

    private int FaltanEn(string pestana, PasoGestionAutomaticaDto paso) => DatosDe(pestana, paso).Count(d => d.Valor is null);

    private static string? Texto(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static string? Fecha(DateOnly? fecha) => fecha?.ToString("dd/MM/yyyy");

    /// <summary>
    /// «DNI sin guiones» (§ 1.1): los portales lo piden pegado y en mayúsculas. Pública solo para poder testearla
    /// directamente (función pura, sin InternalsVisibleTo en CaeManager.Web).
    /// </summary>
    public static string? DniParaPortal(string? dni) =>
        Texto(dni) is { } d ? new string(d.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant() : null;

    /// <summary>
    /// «Completar» (V-1): el Trabajador abre su editor de ficha en un panel, sin salir de la Gestión, y al guardar se
    /// vuelve a leer la Gestión. El código y la dirección del Centro no tienen ese editor: se completan en Centro 360.
    /// </summary>
    private void CompletarTrabajador() => _editorTrabajadorVisible = true;

    private async Task FichaTrabajadorGuardadaAsync()
    {
        _editorTrabajadorVisible = false;
        await CargarAsync();
    }

    /// <summary>Se pueden marcar pasos: con escritura y en una Gestión no sustituida.</summary>
    private bool PuedeMarcar => _puedeEscribir && _detalle is { } d && d.Resumen.Estado != EstadoGestionAutomatica.Sustituida;

    /// <summary>
    /// Opción de «¿Cómo ha quedado?» que no se ofrece. La misma regla para el clic y para su atajo (1, 2). Subido sobre
    /// un paso rechazado lo rechaza el comando (RechazadaSinVersionNueva): hace falta una versión corregida del
    /// Documento. «Esta plataforma no lo pide» se ofrece siempre que se pueda marcar: no afirma nada del Documento.
    /// </summary>
    private bool OpcionDeshabilitada(string clave) =>
        (clave is OpcionSubido or OpcionValidado && _detalle?.Documento.VigenteEnTalveg != true)
        || (clave == OpcionSubido && PasoRechazado);

    /// <summary>Tecla que elige la opción, o null si no tiene o si ahora no se puede elegir (no se anuncia un atajo muerto).</summary>
    private string? AtajoDeOpcion(string clave) => OpcionDeshabilitada(clave)
        ? null
        : clave switch
        {
            OpcionSubido => AtajosPasosGestion.TeclaSubido,
            OpcionValidado => AtajosPasosGestion.TeclaValidado,
            _ => null
        };

    /// <summary>
    /// Atajos del pie (§ 4.5, onKey de la maqueta): → o «s» hacen lo que «Siguiente» sin opción elegida (el siguiente
    /// paso por hacer o, si no queda, la Siguiente Gestión) y no guardan nada; ← es «Anterior»; 1 y 2 eligen
    /// «Subido» y «Validado» con las mismas condiciones que su clic. Con un guardado en vuelo no se atienden (el
    /// guardado abre él mismo el paso que toca), ni con la pantalla de error a la vista, que no enseña los pasos
    /// aunque conserve el detalle anterior. Qué tecla llega y desde dónde lo filtra atajos-pasos-gestion.js.
    /// </summary>
    private async Task AtajoAsync(string tecla)
    {
        if (_detalle is null || _error || _guardando || PasoAbierto is null) return;

        switch (tecla)
        {
            case AtajosPasosGestion.TeclaSiguiente or AtajosPasosGestion.TeclaSiguienteLetra:
                if (SiguientePorHacer() is { } siguiente) Abrir(siguiente.AcreditacionId);
                else await SiguienteGestionAsync();
                break;
            case AtajosPasosGestion.TeclaAnterior:
                Anterior();
                break;
            case AtajosPasosGestion.TeclaSubido:
                ElegirConAtajo(OpcionSubido);
                break;
            case AtajosPasosGestion.TeclaValidado:
                ElegirConAtajo(OpcionValidado);
                break;
        }
    }

    private void ElegirConAtajo(string clave)
    {
        if (PuedeMarcar && !OpcionDeshabilitada(clave)) ElegirOpcion(clave);
    }

    private bool PuedeGuardar => _opcion switch
    {
        OpcionSubido => true,
        OpcionValidado => _hasta is not null || _noSe,
        OpcionRechazado => !string.IsNullOrEmpty(_causa) && !string.IsNullOrWhiteSpace(_motivo),
        // Maqueta (puedeGuardar): «no lo pide» exige marcar «Confirmo que {plataforma} no pide este documento en {Centro}».
        OpcionNoPide => _confirmaNoPide,
        _ => false
    };

    private PasoGestionAutomaticaDto? SiguientePorHacer()
    {
        var lista = Pasos.ToList();
        var i = IndiceAbierto;
        if (i < 0) return lista.FirstOrDefault(p => !p.Hecho);
        return lista.Skip(i + 1).Concat(lista.Take(i)).FirstOrDefault(p => !p.Hecho);
    }

    private string TextoPrimario
    {
        get
        {
            var hay = SiguientePorHacer() is not null;
            return Textos[_opcion is null ? (hay ? "GpSiguiente" : "GpSiguienteGestion") : (hay ? "GpGuardarYSiguiente" : "GpGuardarSiguienteGestion")];
        }
    }

    private async Task CargarAsync()
    {
        // Si Id cambia con esta carga en vuelo, su respuesta es de la Gestión anterior: se descarta.
        var pedida = Id;
        _cargando = _detalle is null;
        _error = false;
        try
        {
            var puedeEscribir = await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion);
            var detalle = await Mediator.Send(new ObtenerGestionAutomaticaQuery(pedida));
            if (pedida != Id) return;
            _puedeEscribir = puedeEscribir;
            _detalle = detalle;
        }
        catch (Exception)
        {
            if (pedida == Id) _error = true;
        }
        finally
        {
            if (pedida == Id) _cargando = false;
        }

        // Un rechazo ya corregido no deja el aviso de reapertura a la vista.
        if (_reabierta && _detalle?.Pasos.Any(p => p.Estado == EstadoAcreditacion.Rechazada) != true)
            _reabierta = false;
    }

    private void Abrir(Guid acreditacionId)
    {
        _abiertoId = acreditacionId;
        _opcion = null;
        _hasta = null;
        _noSe = false;
        _causa = string.Empty;
        _motivo = string.Empty;
        _confirmaNoPide = false;
        _errorGuardado = null;
        _menuAbierto = false;
        _pestana = null;
    }

    /// <summary>
    /// Cambiar de opción borra la confirmación de «no lo pide»: volver a esa opción exige confirmarla otra vez. Elegir
    /// la misma opción no la borra.
    /// </summary>
    private void ElegirOpcion(string clave)
    {
        if (_opcion != clave) _confirmaNoPide = false;
        _opcion = clave;
    }

    private void Anterior()
    {
        var i = IndiceAbierto;
        if (i > 0) Abrir(Pasos[i - 1].AcreditacionId);
    }

    private async Task PrimarioAsync()
    {
        if (_opcion is not null)
        {
            await GuardarAsync(luegoSiguiente: true);
            return;
        }

        if (SiguientePorHacer() is { } siguiente) Abrir(siguiente.AcreditacionId);
        else await SiguienteGestionAsync();
    }

    private Task GuardarSoloAsync() => GuardarAsync(luegoSiguiente: false);

    /// <summary>
    /// El paso cambió desde que se abrió la Gestión: lo dice el comando al comparar la versión vista
    /// (<see cref="MarcarAcreditacionNoRequeridaCommandHandler.CambiadaDesdeQueSeAbrio"/>) o el guardado al chocar con el
    /// token de concurrencia (<see cref="ConcurrenciaOptimista.CodigoConflicto"/>, de <c>ConcurrenciaBehavior</c>).
    /// </summary>
    private static bool EsCambioDesdeQueSeAbrio(Error error) =>
        error.Codigo == MarcarAcreditacionNoRequeridaCommandHandler.CambiadaDesdeQueSeAbrio.Codigo
        || error.Codigo == ConcurrenciaOptimista.CodigoConflicto;

    private async Task GuardarAsync(bool luegoSiguiente)
    {
        var paso = PasoAbierto;
        if (paso is null || _opcion is null || !PuedeGuardar || _guardando || _detalle is null) return;

        _guardando = true;
        _errorGuardado = null;
        var estadoAntes = _detalle.Resumen.Estado;
        var siguiente = luegoSiguiente ? SiguientePorHacer() : null;
        var opcion = _opcion;
        try
        {
            Func<Task>? deshacer = null;
            Result resultado;
            switch (opcion)
            {
                case OpcionSubido:
                    resultado = await Mediator.Send(new MarcarAcreditacionSubidaCommand(paso.AcreditacionId));
                    break;
                case OpcionValidado:
                    var vigencia = _noSe || _hasta is null ? VigenciaEnPlataforma.SinConfirmar : VigenciaEnPlataforma.VenceEl(_hasta.Value);
                    var aceptada = await Mediator.Send(new MarcarAcreditacionAceptadaCommand(paso.AcreditacionId, vigencia));
                    if (aceptada.EsFallido)
                    {
                        resultado = Result.Fallo(aceptada.Error);
                        break;
                    }

                    var previo = aceptada.Valor;
                    deshacer = async () =>
                    {
                        var r = await Mediator.Send(new RestaurarAnotacionAcreditacionCommand(
                            previo.AcreditacionId, previo.EstadoPrevio, previo.VigenciaPrevia, previo.VersionResultante));
                        if (r.EsFallido) Toasts.MostrarError(r.Error);
                    };
                    resultado = Result.Exito();
                    break;
                case OpcionRechazado:
                    if (!Enum.TryParse<CausaRechazoAcreditacion>(_causa, out var causa) || !Causas.Any(c => c.Valor == _causa))
                    {
                        _errorGuardado = Textos["GpErrorGuardadoDescripcion"];
                        return;
                    }

                    resultado = await Mediator.Send(new MarcarAcreditacionRechazadaCommand(paso.AcreditacionId, causa, _motivo.Trim()));
                    break;
                case OpcionNoPide:
                    // La versión que se tenía delante: si cambió entretanto (también la vigencia), el comando no lo sobrescribe a ciegas.
                    resultado = await Mediator.Send(new MarcarAcreditacionNoRequeridaCommand(paso.AcreditacionId, paso.VersionAcreditacion));
                    break;
                default:
                    return;
            }

            if (resultado.EsFallido)
            {
                _errorGuardado = resultado.Error.Mensaje;
                // El paso cambió desde que se abrió: se relee para enseñar cómo está de verdad, y «no lo pide» hay que
                // confirmarlo otra vez sobre ese estado (sin confirmación, «Reintentar» no se ofrece). Es lo mismo si el
                // choque llega en el guardado (el token de EF, ConcurrenciaBehavior): otra escritura entre la lectura
                // del comando y su SaveChanges. Mismo aviso y misma recarga, sea cual sea la opción.
                if (EsCambioDesdeQueSeAbrio(resultado.Error))
                {
                    _errorGuardado = MarcarAcreditacionNoRequeridaCommandHandler.CambiadaDesdeQueSeAbrio.Mensaje;
                    _confirmaNoPide = false;
                    await CargarAsync();
                }

                return;
            }

            if (deshacer is null)
                Toasts.Mostrar(Textos["GpPasoGuardado"].Value, TonoToast.Exito);
            else
            {
                var gestionId = Id;
                var pasoId = paso.AcreditacionId;
                Toasts.Mostrar(Textos["GpPasoGuardado"].Value, TonoToast.Exito, Textos["GpDeshacer"].Value,
                    () => DeshacerAsync(deshacer, gestionId, pasoId));
            }

            _opcion = null;
            _hasta = null;
            _noSe = false;
            _causa = string.Empty;
            _motivo = string.Empty;
            _confirmaNoPide = false;
            await CargarAsync();

            // Un rechazo sobre una Gestión que ya no estaba en curso la reabre (D-6).
            if (opcion == OpcionRechazado && estadoAntes != EstadoGestionAutomatica.EnCurso && _detalle?.Resumen.Estado == EstadoGestionAutomatica.EnCurso)
            {
                _reabierta = true;
                _textoReabierta = Textos["GpRechazadoEn", paso.Plataforma, paso.CentroNombre];
            }

            if (siguiente is not null) Abrir(siguiente.AcreditacionId);
            // Un rechazo reabre la Gestión (D-6): se queda para ver el aviso y «Ir al paso rechazado», no salta a otra.
            else if (luegoSiguiente && opcion != OpcionRechazado) await SiguienteGestionAsync();
        }
        catch (Exception)
        {
            _errorGuardado = Textos["GpErrorGuardadoDescripcion"];
        }
        finally
        {
            _guardando = false;
        }
    }

    /// <summary>
    /// Deshacer del toast: restaura la anotación y reabre el paso. Si entretanto se pasó a otra Gestión («Siguiente
    /// Gestión»), vuelve a la Gestión del paso con ese paso abierto.
    /// </summary>
    private async Task DeshacerAsync(Func<Task> accion, Guid gestionId, Guid pasoId)
    {
        try
        {
            await accion();
        }
        catch (Exception)
        {
            Toasts.Mostrar(Textos["GpErrorTitulo"].Value, TonoToast.Error);
            return;
        }

        if (gestionId != Id)
        {
            Navegacion.NavigateTo($"gestiones/{gestionId}?paso={pasoId}");
            return;
        }

        try
        {
            await InvokeAsync(async () =>
            {
                await CargarAsync();
                Abrir(pasoId);
                StateHasChanged();
            });
        }
        catch (Exception)
        {
            // La pantalla ya no está (se navegó fuera): la anotación ya se restauró y no queda nada que pintar.
        }
    }

    /// <summary>
    /// «Siguiente Gestión» (§ 4.5): la siguiente en curso de la misma Empresa gestionada y después las demás de la
    /// cartera, en el orden del listado. Sin otra en curso, vuelve a Mi trabajo.
    /// </summary>
    private async Task SiguienteGestionAsync()
    {
        try
        {
            var listado = await Mediator.Send(new ObtenerGestionesAutomaticasQuery(EstadoGestionAutomatica.EnCurso));
            var otras = listado.Filas.Where(f => f.Id != Id).ToList();
            var empresa = _detalle?.Resumen.EmpresaGestionadaId;
            var destino = otras.FirstOrDefault(f => empresa is not null && f.EmpresaGestionadaId == empresa) ?? otras.FirstOrDefault();
            Navegacion.NavigateTo(destino is null ? "mi-trabajo" : $"gestiones/{destino.Id}");
        }
        catch (Exception)
        {
            Toasts.Mostrar(Textos["GpErrorTitulo"].Value, TonoToast.Error);
        }
    }
}
