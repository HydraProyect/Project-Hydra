using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Visitas.Commands;
using CaeManager.Application.Visitas.Commands.EnviarPaqueteAcreditacionVisita;
using CaeManager.Application.Visitas.Commands.MarcarDocumentacionGestionada;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Visitas.Pages;

/// <summary>
/// Visita 360, primer incremento («con lo que ya viaja»). Compone con las MISMAS consultas y
/// comandos que el Drawer «Detalle de la visita» del listado —mismo alcance y misma RLS— la
/// tarjeta de identidad, la banda, las pestañas y el lateral. El Drawer se conserva como
/// consulta rápida desde «Ver».
///
/// <para>
/// <b>Qué se carga depende de la rama</b>, igual que en el Drawer: cancelada, nada más; el
/// Centro requiere gestión CAE, la documentación y —solo si se gestiona por correo— la
/// solicitud; el Centro no requiere gestión CAE, el aviso. La rama «requiere gestión CAE y
/// no se gestiona por correo» se ve como hoy (solo la comprobación previa): distinguir
/// plataforma de «sin canal» necesita los canales del Centro, que este detalle no trae.
/// </para>
///
/// <para>
/// <b>Rol Consulta</b> (decisión del 2026-10-08): conserva «Copiar solicitud» y «Copiar
/// aviso». «Enviar por correo» y «Descargar ZIP» no se le pintan, y quien lo decide de verdad
/// es <see cref="ObtenerPaqueteDocumentalVisitaQuery"/>, que le niega el zip.
/// </para>
/// </summary>
public partial class VisitaDetalle : CaeManager.Web.Components.PaginaInteractiva
{
    internal const string PestanaComprobacion = "comprobacion";
    internal const string PestanaPaquete = "paquete";
    internal const string PestanaAviso = "aviso";

    [Parameter] public Guid VisitaId { get; set; }

    /// <summary>Pestaña activa, en la URL (sin parámetro = la primera de la rama).</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private DetalleVisitaDto? _detalle;
    private bool _cargando = true;
    private bool _error;

    /// <summary>
    /// Visita que esta instancia tiene cargada: Blazor reutiliza la instancia al pasar de una
    /// Visita a otra y al cambiar la pestaña en la URL; solo lo primero recarga.
    /// </summary>
    private Guid? _visitaCargada;

    // Abrir otra Visita mientras la anterior aún carga: la respuesta que llegue tarde no
    // puede pintar la Visita equivocada (mismo contador que el Drawer del listado).
    private int _cargaDetalle;
    private int _cargaDocumentacion;

    private DocumentacionVisitaDto? _documentacion;
    private bool _cargandoDocumentacion;
    private bool _errorDocumentacion;

    private AvisoVisitaDto? _aviso;
    private bool _cargandoAviso;
    private string? _errorAviso;

    private SolicitudAccesoCorreoDto? _solicitudCorreo;
    private bool _cargandoSolicitudCorreo;
    private string? _errorSolicitudCorreo;

    private bool _preparandoEnvioPaquete;
    private string? _avisoEnvioPaquete;
    private bool _composerPaqueteVisible;
    private AdjuntoParaEnviarDto? _adjuntoPaquete;

    private bool _marcandoNotificado;
    private bool _marcandoDocumentacionGestionada;
    private (Guid Id, Guid Version)? _visitaDelPaquete;
    private bool _editarVisible;

    private bool _visorVisible;
    private Guid _visorDocumentoId;
    private string _visorTitulo = string.Empty;

    private bool _confirmarCancelarVisible;
    private string _motivoCancelacion = string.Empty;
    private bool _cancelando;

    private bool _confirmarReactivarVisible;
    private string _motivoReactivacion = string.Empty;
    private bool _reactivando;
    private bool _deshaciendoCancelacion;

    private string _pestana = PestanaComprobacion;
    private IReadOnlySet<string> _estadosMarcados = new HashSet<string>();
    private readonly HashSet<string> _gruposPlegados = [];

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["TituloPagina"]), new BreadcrumbElemento(_detalle?.CentroNombre ?? "…")];

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/visitas");
    }

    /// <summary>
    /// Mismo criterio que <c>SoloConEscritura</c>: los roles con escritura. Solo decide qué se
    /// pinta; quien autoriza es cada comando y, para el paquete, su consulta.
    /// </summary>
    private bool _puedeEscribir;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (EstadoAutenticacion is not null)
        {
            var usuario = (await EstadoAutenticacion).User;
            _puedeEscribir = CaeManager.Infrastructure.Identity.Roles.ConEscrituraCsv.Split(',').Any(usuario.IsInRole);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_visitaCargada != VisitaId)
        {
            _visitaCargada = VisitaId;
            await CargarAsync();
        }

        AdoptarPestana();
    }

    // ---------------------------------------------------------------- Ramas

    private bool EsCancelada => _detalle is { EstaCancelada: true };
    private bool EsSinGestionCae => _detalle is { CentroRequiereGestionCae: false };
    private bool EsPorCorreo => _detalle is { CentroRequiereGestionCae: true, CentroGestionadoPorCorreo: true };

    /// <summary>Con gestión CAE y sin cancelar: la única rama que comprueba documentación.</summary>
    private bool CompruebaDocumentacion => _detalle is { EstaCancelada: false, CentroRequiereGestionCae: true };

    private IReadOnlyList<PestanaDefinicion> Pestanas
    {
        get
        {
            if (EsSinGestionCae)
                return [new PestanaDefinicion(PestanaAviso, Textos["SubtituloAvisoVisita"])];

            var comprobacion = new PestanaDefinicion(PestanaComprobacion, Textos["SubtituloComprobacionPrevia"])
            {
                Contador = CompruebaDocumentacion && _documentacion is { } documentacion
                    ? new ContadorPestana(
                        documentacion.Trabajadores.Count,
                        Textos["Visita360ContadorTrabajadores", documentacion.Trabajadores.Count],
                        EnAlerta: documentacion.Trabajadores.Any(t => NoPuedeAcreditarse(t.Documentacion)))
                    : null
            };

            return EsPorCorreo
                ? [comprobacion, new PestanaDefinicion(PestanaPaquete, Textos["Visita360PestanaPaquete"])]
                : [comprobacion];
        }
    }

    private void AdoptarPestana()
    {
        var validas = Pestanas.Select(p => p.Id).ToList();
        _pestana = Pestana is { } pedida && validas.Contains(pedida) ? pedida : validas[0];
    }

    private void CambiarPestana(string pestana)
    {
        _pestana = pestana;
        var primera = Pestanas[0].Id;
        NavigationManager.NavigateTo(
            NavigationManager.GetUriWithQueryParameter("pestana", pestana == primera ? null : pestana), replace: true);
    }

    // ---------------------------------------------------------------- Carga

    private async Task CargarAsync()
    {
        var carga = ++_cargaDetalle;
        ++_cargaDocumentacion;
        _cargando = true;
        _error = false;
        _detalle = null;
        _documentacion = null;
        _errorDocumentacion = false;
        _aviso = null;
        _errorAviso = null;
        _solicitudCorreo = null;
        _errorSolicitudCorreo = null;
        _avisoEnvioPaquete = null;
        _composerPaqueteVisible = false;
        _adjuntoPaquete = null;
        _estadosMarcados = new HashSet<string>();

        try
        {
            var detalle = await Mediator.Send(new ObtenerDetalleVisitaQuery(VisitaId));
            if (carga != _cargaDetalle)
                return;

            // No encontrada y fuera de alcance se ven igual que un fallo de carga: la pantalla
            // no revela si la Visita existe en otra organización.
            if (detalle is null)
            {
                _error = true;
                return;
            }

            _detalle = detalle;
            AdoptarPestana();
            _cargando = false;
            StateHasChanged();

            if (detalle.EstaCancelada)
            {
                // FS-11: sin acciones operativas que cargar para una cancelada.
            }
            else if (detalle.CentroRequiereGestionCae)
            {
                if (detalle.CentroGestionadoPorCorreo)
                    await CargarSolicitudCorreoAsync(carga);
                if (carga == _cargaDetalle)
                    await CargarDocumentacionAsync();
            }
            else
                await CargarAvisoAsync(carga);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (carga == _cargaDetalle)
                _error = true;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargando = false;
        }
    }

    private async Task CargarAvisoAsync(int carga)
    {
        _cargandoAviso = true;
        try
        {
            var resultado = await Mediator.Send(new ObtenerAvisoVisitaQuery(VisitaId));
            if (carga != _cargaDetalle) return;

            if (resultado.EsFallido)
                _errorAviso = resultado.Error.Mensaje;
            else
                _aviso = resultado.Valor;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (carga == _cargaDetalle)
                _errorAviso = null;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargandoAviso = false;
        }
    }

    private async Task CargarSolicitudCorreoAsync(int carga)
    {
        _cargandoSolicitudCorreo = true;
        try
        {
            var resultado = await Mediator.Send(new ObtenerSolicitudAccesoCorreoQuery(VisitaId));
            if (carga != _cargaDetalle) return;

            if (resultado.EsFallido)
                _errorSolicitudCorreo = resultado.Error.Mensaje;
            else
                _solicitudCorreo = resultado.Valor;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (carga == _cargaDetalle)
                _errorSolicitudCorreo = null;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargandoSolicitudCorreo = false;
        }
    }

    private async Task CargarDocumentacionAsync()
    {
        var carga = ++_cargaDocumentacion;
        _cargandoDocumentacion = true;
        _errorDocumentacion = false;
        _documentacion = null;

        try
        {
            var documentacion = await Mediator.Send(new ObtenerDocumentacionVisitaQuery(VisitaId));
            if (carga == _cargaDocumentacion)
                _documentacion = documentacion;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (carga == _cargaDocumentacion)
                _errorDocumentacion = true;
        }
        finally
        {
            if (carga == _cargaDocumentacion)
                _cargandoDocumentacion = false;
        }
    }

    // ---------------------------------------------------------------- Cabecera

    /// <summary>Día de negocio (Europe/Madrid) de la carga de la página.</summary>
    private static DateOnly Hoy => DiaDeNegocio.Hoy();

    private static string TextoFechas(DateOnly inicio, DateOnly fin) =>
        inicio == fin ? inicio.ToString("dd/MM/yyyy") : $"{inicio:dd/MM/yyyy} – {fin:dd/MM/yyyy}";

    private string TextoPlazo(DetalleVisitaDto visita)
    {
        // Una Visita de varios días que acaba hoy: «termina hoy» dice más que «en curso».
        if (visita.FechaFin == Hoy && visita.FechaInicio != Hoy)
            return Textos["Visita360PlazoTerminaHoy"].Value;

        var (plazo, dias) = AntelacionVisitaUi.Plazo(visita.FechaInicio, visita.FechaFin, Hoy);
        return plazo switch
        {
            PlazoVisita.Hoy => Textos["PlazoHoy"].Value,
            PlazoVisita.Manana => Textos["PlazoManana"].Value,
            PlazoVisita.EnDias => Textos["PlazoEnDias", dias].Value,
            PlazoVisita.EnCurso => Textos["PlazoEnCurso"].Value,
            _ => Textos["PlazoFinalizada"].Value
        };
    }

    /// <summary>La banda empieza por el plazo, como frase: «Termina hoy.»</summary>
    private string TextoPlazoBanda(DetalleVisitaDto visita)
    {
        var plazo = TextoPlazo(visita);
        return plazo.Length == 0 ? plazo : char.ToUpper(plazo[0], System.Globalization.CultureInfo.CurrentCulture) + plazo[1..] + ".";
    }

    private string TextoOrigen(OrigenVisita origen) => origen switch
    {
        OrigenVisita.Correo => Textos["OrigenCorreo"].Value,
        OrigenVisita.WhatsApp => Textos["OrigenWhatsApp"].Value,
        OrigenVisita.Manual => Textos["OrigenManual"].Value,
        _ => Textos["OrigenPlataforma"].Value
    };

    // ---------------------------------------------------------------- Anillo y banda

    /// <summary>
    /// Un Trabajador está «listo» cuando toda la documentación que el Centro le exige está al
    /// día. «Al día» es la regla de los porcentajes (<see cref="CumplimientoDocumental.EsConforme(EstadoDocumento)"/>:
    /// Próximo y Urgente cuentan, Sin confirmar no) más «En tolerancia», que aquí ya llega resuelto
    /// con la tolerancia del Centro de la Visita (decisión del 2026-10-03). Cuenta solo la
    /// documentación del propio Trabajador, como el anillo del mockup.
    /// </summary>
    public static bool EstaListo(SeccionDocumentacionDto documentacion) =>
        documentacion.Documentos.All(d =>
            CumplimientoDocumental.EsConforme(d.Estado) || d.Estado is EstadoDocumento.EnTolerancia);

    private bool HayFiltrosActivos => _estadosMarcados.Count > 0;

    /// <summary>Las incidencias de un titular con la coma que las separa en la banda.</summary>
    private static IEnumerable<(string Separador, DocumentoVisitaItemDto Documento)> ConSeparador(
        IReadOnlyList<DocumentoVisitaItemDto> documentos) =>
        documentos.Select((documento, indice) => (indice > 0 ? ", " : string.Empty, documento));

    /// <summary>Algún documento exigido vencido o sin presentar: lo que la banda nombra y lo que pone en alerta el contador.</summary>
    public static bool NoPuedeAcreditarse(SeccionDocumentacionDto documentacion) =>
        documentacion.Documentos.Any(EsIncidencia);

    private static bool EsIncidencia(DocumentoVisitaItemDto documento) =>
        documento.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante;

    private (int Listos, int Total, int Porcentaje)? Anillo
    {
        get
        {
            if (!CompruebaDocumentacion || _documentacion is not { Trabajadores.Count: > 0 } documentacion)
                return null;

            var total = documentacion.Trabajadores.Count;
            var listos = documentacion.Trabajadores.Count(t => EstaListo(t.Documentacion));
            return (listos, total, (int)Math.Round(listos * 100d / total, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>Quién tiene incidencias y cuáles, en el orden de la lista: del peor estado al mejor.</summary>
    private IReadOnlyList<(string Titular, IReadOnlyList<DocumentoVisitaItemDto> Incidencias)> IncidenciasDeBanda
    {
        get
        {
            if (!CompruebaDocumentacion || _documentacion is not { Trabajadores.Count: > 0 } documentacion)
                return [];

            var partes = new List<(string, IReadOnlyList<DocumentoVisitaItemDto>)>();
            foreach (var trabajador in documentacion.Trabajadores.OrderBy(t => SeveridadEstadoDocumento.Rango(t.Documentacion.PeorEstado)))
            {
                var incidencias = trabajador.Documentacion.Documentos.Where(EsIncidencia).ToList();
                if (incidencias.Count > 0)
                    partes.Add((trabajador.NombreCompleto, incidencias));
            }

            var deEmpresa = documentacion.Empresa.Documentos.Where(EsIncidencia).ToList();
            if (deEmpresa.Count > 0)
                partes.Add((Textos["SeccionDocumentacionEmpresa"].Value, deEmpresa));

            return partes;
        }
    }

    /// <summary>Sin escritura la incidencia no lleva delegado y la banda la pinta como texto.</summary>
    private EventCallback AlPulsarIncidencia(DocumentoVisitaItemDto documento) =>
        _puedeEscribir ? EventCallback.Factory.Create(this, () => AbrirDocumento(documento)) : default;

    private string TextoIncidencia(DocumentoVisitaItemDto documento) =>
        Textos["Visita360BandaIncidencia", documento.TipoDocumentoNombre, EstadoDocumentoVisitaUi.Texto(documento.Estado, Textos)].Value;

    // ---------------------------------------------------------------- Comprobación previa

    /// <summary>Un grupo de la lista: la documentación de la Empresa o la de un Trabajador que entra.</summary>
    private sealed record GrupoComprobacion(
        string Id, string Nombre, string Empleador, EstadoDocumento PeorEstado,
        IReadOnlyList<DocumentoVisitaItemDto> Documentos, IReadOnlyList<DocumentoVisitaItemDto> Visibles);

    private static string ClaveEstado(EstadoDocumento estado) => EstadoDocumentoVisitaUi.Clave(estado);

    private IEnumerable<DocumentoVisitaItemDto> TodosLosDocumentos(DocumentacionVisitaDto documentacion) =>
        documentacion.Empresa.Documentos.Concat(documentacion.Trabajadores.SelectMany(t => t.Documentacion.Documentos));

    /// <summary>Un contador por estado presente, del peor al mejor, con el vocabulario de las páginas 360.</summary>
    private IReadOnlyList<OpcionEstadoRecuento> OpcionesEstado(DocumentacionVisitaDto documentacion) =>
        TodosLosDocumentos(documentacion)
            .GroupBy(d => ClaveEstado(d.Estado))
            .OrderBy(g => g.Min(d => SeveridadEstadoDocumento.Rango(d.Estado)))
            .Select(g => new OpcionEstadoRecuento(g.Key, EstadoDocumentoVisitaUi.Texto(g.First().Estado, Textos), g.Count()))
            .ToList();

    private bool Visible(DocumentoVisitaItemDto documento) =>
        _estadosMarcados.Count == 0 || _estadosMarcados.Contains(ClaveEstado(documento.Estado));

    private static int ContarVisibles(IReadOnlyList<GrupoComprobacion> grupos) => grupos.Sum(g => g.Visibles.Count);

    /// <summary>
    /// La Empresa y cada Trabajador, del peor estado al mejor; a igualdad, el orden por
    /// apellidos que trae la consulta (OrderBy es estable). Con un filtro marcado, el grupo
    /// que se queda sin filas no se pinta.
    /// </summary>
    private IReadOnlyList<GrupoComprobacion> Grupos(DocumentacionVisitaDto documentacion)
    {
        var empresa = _detalle?.EmpresaRazonSocial ?? string.Empty;
        var grupos = documentacion.Trabajadores
            .Select(t => new GrupoComprobacion(
                t.TrabajadorId.ToString(), t.NombreCompleto, t.EmpleadorNombre, t.Documentacion.PeorEstado,
                t.Documentacion.Documentos, t.Documentacion.Documentos.Where(Visible).ToList()))
            .Append(new GrupoComprobacion(
                "empresa", Textos["SeccionDocumentacionEmpresa"], empresa, documentacion.Empresa.PeorEstado,
                documentacion.Empresa.Documentos, documentacion.Empresa.Documentos.Where(Visible).ToList()))
            .OrderBy(g => SeveridadEstadoDocumento.Rango(g.PeorEstado));

        return (_estadosMarcados.Count == 0 ? grupos : grupos.Where(g => g.Visibles.Count > 0)).ToList();
    }

    /// <summary>
    /// Tiene algo pendiente el Trabajador que no está «listo» (<see cref="EstaListo"/>), para que
    /// el resumen y el anillo de la cabecera cuenten lo mismo. No dice «no puede entrar»: la pantalla sabe el estado de los documentos, no lo
    /// que decide el control de acceso del Centro.
    /// </summary>
    private string? ResumenComprobacion(DocumentacionVisitaDto documentacion)
    {
        var total = documentacion.Trabajadores.Count;
        if (total == 0)
            return null;

        var pendientes = documentacion.Trabajadores.Count(t => !EstaListo(t.Documentacion));

        if (pendientes == 0)
            return total == 1
                ? Textos["ResumenSinPendientesUno"].Value
                : Textos["ResumenSinPendientesVarios", total].Value;

        return pendientes == 1
            ? Textos["ResumenPendientesUno", total].Value
            : Textos["ResumenPendientesVarios", pendientes, total].Value;
    }

    private string DetalleDocumento(DocumentoVisitaItemDto documento) => documento switch
    {
        { Estado: EstadoDocumento.Faltante } => Textos["Visita360FilaSinDocumento"].Value,
        { Estado: EstadoDocumento.SinCaducidad } => Textos["Visita360FilaNoCaduca"].Value,
        { FechaVencimiento: null } => Textos["Visita360FilaSinFecha"].Value,
        { FechaVencimiento: { } fecha } when fecha < Hoy => Textos["Visita360FilaVencio", fecha].Value,
        { FechaVencimiento: { } fecha } => Textos["Visita360FilaVence", fecha].Value
    };

    private void AlternarGrupo(string id, bool abierto)
    {
        if (abierto)
            _gruposPlegados.Remove(id);
        else
            _gruposPlegados.Add(id);
    }

    /// <summary>
    /// Igual que el Drawer: un documento con archivo abre el visor en la misma pantalla; sin
    /// archivo va a editarlo; un hueco lleva al alta con el propietario y el tipo ya elegidos.
    /// </summary>
    private void AbrirDocumento(DocumentoVisitaItemDto item)
    {
        if (item.DocumentoId is { } documentoId)
        {
            if (item.ArchivoUrl is null)
            {
                NavigationManager.NavigateTo($"/documentos?documentoId={documentoId}");
                return;
            }

            _visorDocumentoId = documentoId;
            _visorTitulo = item.TipoDocumentoNombre;
            _visorVisible = true;
            return;
        }

        if (item.TrabajadorId is { } trabajadorId)
        {
            NavigationManager.NavigateTo($"/documentos?trabajadorId={trabajadorId}&tipoDocumentoId={item.TipoDocumentoId}");
            return;
        }

        NavigationManager.NavigateTo($"/documentos?empresaIdFaltante={_documentacion!.EmpresaId}&tipoDocumentoId={item.TipoDocumentoId}");
    }

    // ---------------------------------------------------------------- Solicitud, aviso y paquete

    /// <summary>Lo que se pega en el correo: asunto en la primera línea y el cuerpo debajo; los destinatarios se muestran aparte.</summary>
    private static string TextoSolicitudParaCopiar(SolicitudAccesoCorreoDto solicitud) =>
        solicitud.Asunto + "\n\n" + solicitud.Cuerpo;

    private static string TextoAvisoParaCopiar(AvisoVisitaDto aviso) =>
        aviso.Asunto + "\n\n" + aviso.Cuerpo;

    private static string RutaPaqueteDocumental(Guid visitaId) => $"/visitas/{visitaId}/paquete-documental.zip";

    /// <summary>
    /// «Enviar por correo»: construye el paquete con la misma consulta que la descarga
    /// (<see cref="ObtenerPaqueteDocumentalVisitaQuery"/>: autoriza por alcance y por rol, aplica
    /// la selección de qué viaja y registra el acceso a documentos sensibles) y abre el
    /// compositor. Si el ZIP supera lo que admite un correo, no abre nada: avisa con la descarga
    /// como alternativa.
    /// </summary>
    private async Task AbrirEnviarPaquetePorCorreoAsync()
    {
        if (_detalle is not { } detalle || _solicitudCorreo is null || _preparandoEnvioPaquete)
            return;

        var carga = _cargaDetalle;
        _preparandoEnvioPaquete = true;
        _avisoEnvioPaquete = null;
        try
        {
            var resultado = await Mediator.Send(new ObtenerPaqueteDocumentalVisitaQuery(detalle.Id));
            if (carga != _cargaDetalle) return;

            if (resultado.EsFallido)
            {
                AvisarDelEnvio(resultado.Error.Mensaje);
                return;
            }

            var paquete = resultado.Valor;
            if (paquete.Contenido.LongLength > LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)
            {
                AvisarDelEnvio(Textos["PaqueteDemasiadoGrandeParaCorreo",
                    MegabytesParaMostrar(paquete.Contenido.LongLength),
                    MegabytesParaMostrar(LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)].Value);
                return;
            }

            _adjuntoPaquete = new AdjuntoParaEnviarDto(paquete.NombreArchivo, "application/zip", paquete.Contenido);
            _visitaDelPaquete = (detalle.Id, detalle.Version);
            _composerPaqueteVisible = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (carga == _cargaDetalle)
                AvisarDelEnvio(Textos["ErrorEnviarPaquetePorCorreo"].Value);
        }
        finally
        {
            _preparandoEnvioPaquete = false;
        }
    }

    /// <summary>
    /// El aviso vive junto a los botones de la pestaña «Paquete de acreditación»; «Enviar por
    /// correo» también está en la cabecera, así que desde otra pestaña se dice además con un toast.
    /// </summary>
    private void AvisarDelEnvio(string mensaje)
    {
        _avisoEnvioPaquete = mensaje;
        if (_pestana != PestanaPaquete)
            ToastService.Mostrar(mensaje, TonoToast.Error);
    }

    /// <summary>Redondeo hacia arriba a un decimal: un ZIP de 3,01 MB no puede mostrarse como «3 MB» junto a un tope de 3 MB.</summary>
    private static string MegabytesParaMostrar(long bytes) =>
        (Math.Ceiling(bytes / (1024d * 1024d) * 10) / 10).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);

    // ---------------------------------------------------------------- Marcar como notificada

    /// <summary>
    /// Mismo comando que el interruptor del listado: solo cambia la marca, no envía ningún
    /// aviso. Si falla, se recarga la Visita para que lo que se ve sea lo que hay.
    /// </summary>
    private async Task AlternarNotificadoAsync()
    {
        if (_detalle is not { EstaCancelada: false } detalle || _marcandoNotificado)
            return;

        var notificado = !detalle.NotificadoCliente;
        _marcandoNotificado = true;

        try
        {
            var resultado = await Mediator.Send(new MarcarNotificadoClienteCommand(detalle.Id, notificado));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                await CargarAsync();
                return;
            }

            // Si mientras tanto se abrió otra Visita, su detalle no se toca.
            if (_detalle?.Id == detalle.Id)
                _detalle = _detalle with { NotificadoCliente = notificado };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToastService.Mostrar(Textos["ToastErrorNotificacion"], TonoToast.Error);
            await CargarAsync();
        }
        finally
        {
            _marcandoNotificado = false;
        }
    }

    // ---------------------------------------------------------------- Documentación gestionada

    /// <summary>
    /// El compositor no envía un mensaje suelto: envía el paquete de ESTA Visita, y el comando de
    /// Application la deja con la documentación gestionada. Viajan el Id y la versión de la Visita
    /// para la que se preparó el paquete, no los de la que esté abierta al pulsar «Enviar».
    /// </summary>
    private Task<Result<Guid>> EnviarPaqueteDeLaVisitaAsync(EnviarMensajeNuevoCommand mensaje) =>
        _visitaDelPaquete is not { } visita
            ? Task.FromResult(Result.Fallo<Guid>(AutorizacionCancelacionVisita.NoEncontrada))
            : Mediator.Send(new EnviarPaqueteAcreditacionVisitaCommand(
                visita.Id, visita.Version, mensaje.ConexionIntegracionId, mensaje.Destinatarios, mensaje.Asunto, mensaje.CuerpoHtml,
                mensaje.Adjuntos ?? []));

    /// <summary>
    /// Salida manual de «Por gestionar». Lleva la versión con la que se abrió la página: si
    /// alguien añadió o quitó un Trabajador mientras tanto, el servidor lo rechaza. Tanto si sale
    /// bien como si falla, la Visita se vuelve a leer.
    /// </summary>
    private async Task MarcarDocumentacionGestionadaAsync()
    {
        if (_detalle is not { EstaCancelada: false } detalle || _marcandoDocumentacionGestionada)
            return;

        _marcandoDocumentacionGestionada = true;
        try
        {
            var resultado = await Mediator.Send(new MarcarDocumentacionGestionadaCommand(detalle.Id, detalle.Version));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(Textos["ToastDocumentacionGestionada"], TonoToast.Exito);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToastService.Mostrar(Textos["ToastErrorDocumentacionGestionada"], TonoToast.Error);
        }
        finally
        {
            _marcandoDocumentacionGestionada = false;
        }

        await CargarAsync();
    }

    private string TextoDocumentacionGestionada(DetalleVisitaDto detalle) =>
        detalle.DocumentacionGestionadaEnUtc is { } gestionadaEn
            ? Textos["DetalleDocumentacionGestionadaEl", DiaDeNegocio.De(gestionadaEn)].Value
            : Textos["BadgeDocumentacionPorGestionar"].Value;

    // ---------------------------------------------------------------- Cancelar, deshacer y reactivar

    /// <summary>
    /// Regla del 2026-09-29 (toda pérdida de edición pregunta): un diálogo de confirmación con
    /// un «Motivo (opcional)» escrito pregunta antes de descartarlo. Solo cuenta con el diálogo
    /// abierto, y un motivo en blanco no es nada escrito.
    /// </summary>
    private static bool HayMotivoSinConfirmar(bool dialogoAbierto, string motivo) =>
        dialogoAbierto && !string.IsNullOrWhiteSpace(motivo);

    private bool HayMotivoEnAlgunDialogo() =>
        HayMotivoSinConfirmar(_confirmarCancelarVisible, _motivoCancelacion)
        || HayMotivoSinConfirmar(_confirmarReactivarVisible, _motivoReactivacion);

    private void CerrarDialogosDescartando()
    {
        _confirmarCancelarVisible = false;
        _confirmarReactivarVisible = false;
    }

    private void AbrirCancelar()
    {
        _motivoCancelacion = string.Empty;
        _confirmarCancelarVisible = true;
    }

    private async Task ConfirmarCancelarAsync()
    {
        _cancelando = true;

        try
        {
            var resultado = await Mediator.Send(new CancelarVisitaCommand(VisitaId, _motivoCancelacion));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
            }
            else
            {
                // FS-11: el aviso ofrece deshacer, que es reactivarla sin motivo y con la
                // versión que dejó la cancelación (el recibo), no con la que la página tenga luego.
                var recibo = resultado.Valor;
                ToastService.Mostrar(Textos["ToastCancelada"], TonoToast.Exito,
                    Textos["ToastAccionDeshacer"].Value, () => DeshacerCancelarAsync(recibo));
                _confirmarCancelarVisible = false;
                await CargarAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToastService.Mostrar(Textos["ToastErrorCancelar"], TonoToast.Error);
        }
        finally
        {
            _cancelando = false;
        }
    }

    /// <summary>
    /// «Deshacer» del aviso de cancelar (FS-11): reactiva sin motivo y con la versión del recibo
    /// de la cancelación. Si alguien cambió la Visita entretanto, el comando lo rechaza y el
    /// aviso lo dice en vez de pisar ese cambio.
    /// </summary>
    private async Task DeshacerCancelarAsync(VisitaCanceladaDto recibo)
    {
        if (_deshaciendoCancelacion) return;
        _deshaciendoCancelacion = true;

        try
        {
            var resultado = await Mediator.Send(new ReactivarVisitaCommand(recibo.Id, recibo.VersionResultante));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(Textos["ToastReactivada"], TonoToast.Exito);

            // También con error: un rechazo por versión significa que la Visita cambió y la página no lo sabe.
            if (_visitaCargada == recibo.Id)
            {
                await CargarAsync();
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToastService.Mostrar(Textos["ToastErrorReactivar"], TonoToast.Error);
        }
        finally
        {
            _deshaciendoCancelacion = false;
        }
    }

    private void AbrirReactivar()
    {
        _motivoReactivacion = string.Empty;
        _confirmarReactivarVisible = true;
    }

    private async Task ConfirmarReactivarAsync()
    {
        if (_detalle is not { } detalle)
            return;

        _reactivando = true;

        try
        {
            var resultado = await Mediator.Send(new ReactivarVisitaCommand(detalle.Id, detalle.Version, _motivoReactivacion));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);

                // La Visita cambió desde que se vio: se cierra el diálogo y se recarga para que lo
                // que se ve (y la versión que se enviará la próxima vez) sea lo que hay.
                if (resultado.Error.Codigo == ConcurrenciaOptimista.CodigoConflicto)
                {
                    _confirmarReactivarVisible = false;
                    await CargarAsync();
                }
            }
            else
            {
                ToastService.Mostrar(Textos["ToastReactivada"], TonoToast.Exito);
                _confirmarReactivarVisible = false;
                await CargarAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToastService.Mostrar(Textos["ToastErrorReactivar"], TonoToast.Error);
        }
        finally
        {
            _reactivando = false;
        }
    }
}
