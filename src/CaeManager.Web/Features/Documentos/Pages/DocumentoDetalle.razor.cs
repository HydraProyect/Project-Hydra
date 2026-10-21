using CaeManager.Application.Documentos.Queries.ObtenerFichaDocumento360;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Documentos.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Documentos.Pages;

/// <summary>
/// Documento 360 — página (mockup «Documento 360 TALVEG», paquete 10-20): un documento concreto con sus pestañas «Dónde se
/// exige» (los Centros que exigen su tipo a su propietario), «Versiones» (los documentos del mismo propietario y tipo),
/// «Validación», «Firma», «Ficha» e «Historial». El panel lateral del Documento se conserva como consulta rápida.
///
/// <para>
/// Todo sale de una sola consulta, <see cref="ObtenerFichaDocumento360Query"/>, que es también la que autoriza (el alcance de
/// lectura del propietario, el mismo de <c>ObtenerDocumentoPorIdQuery</c>): quien no puede leer el documento recibe <c>null</c> y ve el
/// mismo estado que con uno que no existe. La Validación se pide al abrir su pestaña. La ruta no restringe roles (el panel
/// tampoco): Renovar, Editar y Reclamar van tras <c>SoloConEscritura</c>, y el comando decide.
/// </para>
///
/// <para>
/// Fuera de este incremento (en el mockup, no hechos): «Editar documento» y «Eliminar documento» del menú, la Nota interna del
/// lateral (el documento no tiene campo de nota), el conmutador «Recomendaciones» (la recomendación sale siempre que
/// hay datos), el CSV en la cabecera, las tarjetas de la Ficha más allá del Propietario, «Dónde se exige» de Vehículo,
/// Cliente y Proyecto (no tienen exigencia por Centro) y la paginación de las listas.
/// </para>
/// </summary>
public partial class DocumentoDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    internal const string PestanaDondeSeExige = "exige";
    internal const string PestanaVersiones = "versiones";
    internal const string PestanaValidacion = "validacion";
    internal const string PestanaFirma = "firma";
    internal const string PestanaFicha = "ficha";
    internal const string PestanaHistorial = "historial";

    [Parameter] public Guid DocumentoId { get; set; }

    /// <summary>La pestaña, en la URL, como en el resto de fichas 360: recargar o compartir el enlace recupera lo que se estaba mirando.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private DrawerGestionDocumento _drawerGestion = default!;

    private FichaDocumento360Dto? _ficha;
    private bool _cargando = true;
    private bool _error;
    /// <summary>La consulta devolvió null: no existe o no se ve (§ 4.5). No es transitorio, así que no lleva «Reintentar».</summary>
    private bool _noEncontrada;
    private Guid _documentoCargado;
    private int _carga;

    private IReadOnlySet<string> _estadosMarcados = new HashSet<string>();
    private Guid? _versionAbierta;
    private bool _reclamacionVisible;

    private ValidacionOficialDocumentoDto? _validacion;
    private bool _cargandoValidacion;
    private bool _validacionPedida;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
    [
        new(Textos["MigaDocumentos"]),
        new(_ficha?.Documento.TipoDocumentoNombre ?? Textos["MigaCargando"])
    ];

    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaDondeSeExige, Textos["PestanaDondeSeExige"])
        {
            Contador = new ContadorPestana(
                _ficha?.DondeSeExige.Count ?? 0, Textos["GlosaDondeSeExige"],
                EnAlerta: (_ficha?.DondeSeExige ?? []).Any(c => !CumplimientoDocumental.EsConforme(c.Estado)))
        },
        new(PestanaVersiones, Textos["PestanaVersiones"])
        {
            Contador = new ContadorPestana(_ficha?.Versiones.Count ?? 0, Textos["GlosaVersiones"])
        },
        new(PestanaValidacion, Textos["PestanaValidacion"]),
        new(PestanaFirma, Textos["PestanaFirma"]),
        new(PestanaFicha, Textos["PestanaFicha"]),
        new(PestanaHistorial, Textos["PestanaHistorial"])
    ];

    /// <summary>La pestaña que se enseña. Un <c>?pestana=</c> desconocido cae en «Dónde se exige», la de por defecto, que no se escribe en la URL.</summary>
    private string PestanaActiva => Pestanas.Any(p => p.Id == Pestana) ? Pestana! : PestanaDondeSeExige;

    private void CambiarPestana(string id)
    {
        Pestana = Pestanas.Any(p => p.Id == id) ? id : PestanaDondeSeExige;
        NavigationManager.ActualizarFiltroEnUrl("pestana", Pestana == PestanaDondeSeExige ? null : Pestana);
        _ = PedirValidacionSiHaceFaltaAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_documentoCargado != DocumentoId)
        {
            _documentoCargado = DocumentoId;
            _ficha = null;
            _validacion = null;
            _validacionPedida = false;
            _estadosMarcados = new HashSet<string>();
            _versionAbierta = null;
            _reclamacionVisible = false;
            await CargarAsync();
        }

        await PedirValidacionSiHaceFaltaAsync();
    }

    /// <summary>Carga (o recarga) la ficha. Una respuesta que llega después de otra petición más nueva se descarta.</summary>
    private async Task CargarAsync()
    {
        var carga = ++_carga;
        _cargando = _ficha is null;
        _error = false;
        // Desmonta FichaNoEncontrada de un Id anterior: «Atrás»/«Adelante» reutilizan la página.
        _noEncontrada = false;

        try
        {
            var ficha = await Mediator.Send(new ObtenerFichaDocumento360Query(DocumentoId), _ciclo.Token);
            if (carga != _carga) return;

            // null = no existe o quien pregunta no puede leerla: la página no distingue.
            _ficha = ficha;
            _noEncontrada = ficha is null;
            // Tras renovar, la validación del archivo nuevo es otra.
            _validacion = null;
            _validacionPedida = false;
            await PedirValidacionSiHaceFaltaAsync();
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (carga == _carga)
            {
                _ficha = null;
                _noEncontrada = false;
                _error = true;
            }
        }
        finally
        {
            if (carga == _carga)
                _cargando = false;
        }
    }

    private async Task PedirValidacionSiHaceFaltaAsync()
    {
        if (_ficha is null || PestanaActiva != PestanaValidacion || _validacionPedida) return;
        _validacionPedida = true;
        _cargandoValidacion = true;
        var carga = _carga;
        try
        {
            var validacion = await Mediator.Send(new ObtenerValidacionOficialDocumentoQuery(DocumentoId), _ciclo.Token);
            if (carga != _carga) return;
            _validacion = validacion;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Sin validación se ve igual que un documento sin perfil oficial: la pestaña lo explica.
        }
        finally
        {
            if (carga == _carga)
            {
                _cargandoValidacion = false;
                await InvokeAsync(StateHasChanged);
            }
        }
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

    /// <summary>Renovar: el formulario del documento (su serie de renovación, o la edición de sus datos si ya está sustituido).</summary>
    private Task RenovarAsync(Guid documentoId) => _drawerGestion.AbrirEditarAsync(documentoId);

    /// <summary>A quién se reclama este documento: su Trabajador o su Empresa titular; null en el resto de ámbitos.</summary>
    private (AmbitoAplicacion Ambito, Guid EntidadId)? TitularReclamable => _ficha?.Documento switch
    {
        { Ambito: AmbitoAplicacion.Trabajador, TrabajadorId: { } trabajadorId } => (AmbitoAplicacion.Trabajador, trabajadorId),
        { Ambito: AmbitoAplicacion.Empresa, EmpresaId: { } empresaId } => (AmbitoAplicacion.Empresa, empresaId),
        _ => null,
    };

    private string? RutaDelPropietario => _ficha?.Documento switch
    {
        { Ambito: AmbitoAplicacion.Trabajador, TrabajadorId: { } trabajadorId } => $"/trabajadores/{trabajadorId}",
        { Ambito: AmbitoAplicacion.Empresa, EmpresaId: { } empresaId } => $"/empresas/{empresaId}",
        _ => null,
    };

    private string IconoDelPropietario => _ficha?.Documento.Ambito switch
    {
        AmbitoAplicacion.Trabajador => "trabajadores",
        AmbitoAplicacion.Vehiculo => "vehiculos",
        _ => "empresas",
    };

    // ----- Cabecera -----

    private int CentrosValidos => (_ficha?.DondeSeExige ?? []).Count(c => CumplimientoDocumental.EsConforme(c.Estado));

    private int? PorcentajeDeCentros => _ficha is { DondeSeExige.Count: > 0 } ficha
        ? (int)Math.Round(100.0 * CentrosValidos / ficha.DondeSeExige.Count)
        : null;

    /// <summary>«Cerrada» si otro documento lo sustituyó; si no, su estado de vigencia.</summary>
    private string TextoDelDocumento => _ficha?.Documento.SustitutoId is not null
        ? Textos["VersionCerrada"]
        : EstadoDocumentoFicha360.Texto(_ficha?.Estado ?? EstadoDocumento.Vigente);

    private TonoBadge TonoDelDocumento => _ficha?.Documento.SustitutoId is not null
        ? TonoBadge.Neutro
        : EstadoDocumentoFicha360.Tono(_ficha?.Estado ?? EstadoDocumento.Vigente);

    private string TextoVencimiento(FichaDocumento360Dto ficha)
    {
        var documento = ficha.Documento;
        if (documento.FechaVencimiento is not { } vencimiento)
            return documento.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? Textos["MetaSinCaducidad"] : Textos["MetaSinConfirmar"];
        var clave = ficha.Estado == EstadoDocumento.Vencido ? "MetaVencio" : "MetaVence";
        return $"{Textos[clave]} {Fecha(vencimiento)}";
    }

    // ----- Dónde se exige -----

    private IReadOnlyList<OpcionEstadoRecuento> OpcionesDeEstado =>
        (_ficha?.DondeSeExige ?? [])
        .GroupBy(c => EstadoDocumentoFicha360.Clave(c.Estado))
        .OrderBy(g => EstadoTipoDocumentoCalculo.Gravedad(g.First().Estado))
        .Select(g => new OpcionEstadoRecuento(g.Key, EstadoDocumentoFicha360.Texto(g.First().Estado), g.Count()))
        .ToList();

    private IReadOnlyList<CentroQueExigeDocumentoDto> CentrosVisibles =>
        (_ficha?.DondeSeExige ?? [])
        .Where(c => _estadosMarcados.Count == 0 || _estadosMarcados.Contains(EstadoDocumentoFicha360.Clave(c.Estado)))
        .ToList();

    private bool HayFiltrosActivos => _estadosMarcados.Count > 0;

    private void CambiarEstados(IReadOnlySet<string> marcados) => _estadosMarcados = marcados;

    private void QuitarFiltros() => _estadosMarcados = new HashSet<string>();

    /// <summary>«Cliente empresarial · Plataforma», lo que se sabe del Centro además de su nombre.</summary>
    private static string LineaDeCentro(CentroQueExigeDocumentoDto centro) =>
        string.Join(" · ", new[] { centro.ClienteNombre, centro.PlataformaNombre }.Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Lo que condiciona el acceso en ese Centro: que bloquee, o los días de tolerancia que da.</summary>
    private string CifraNegrita(CentroQueExigeDocumentoDto centro) =>
        centro.BloqueaAcceso ? Textos["CifraBloquea"]
        : centro.ToleranciaDias > 0 ? Textos[centro.ToleranciaDias == 1 ? "CifraToleranciaUno" : "CifraTolerancia", centro.ToleranciaDias]
        : Textos["CifraNoBloquea"];

    private string CifraTexto(CentroQueExigeDocumentoDto centro) =>
        centro.BloqueaAcceso || centro.ToleranciaDias == 0 ? Textos["CifraBloqueaTexto"] : Textos["CifraToleranciaTexto"];

    /// <summary>
    /// Solo cuando sale de datos que ya existen: un documento que no vale y que bloquea el acceso a ese Centro. En cualquier otro
    /// caso no hay recomendación (no se inventa una).
    /// </summary>
    private string? TextoRecomendacion(CentroQueExigeDocumentoDto centro) =>
        centro.BloqueaAcceso && centro.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante
            ? Textos["RecomendacionRenovar"]
            : null;

    // ----- Versiones -----

    /// <summary>«10/07/2026 → 10/08/2026 · versión actual»; en una cerrada, sus comentarios en vez de «versión actual».</summary>
    private string LineaDeVersion(VersionDocumentoDto version)
    {
        var fechas = version.FechaVencimiento is { } vencimiento
            ? $"{Fecha(version.FechaEmision)} → {Fecha(vencimiento)}"
            : Fecha(version.FechaEmision);
        var cola = version.EsActual ? Textos["VersionActual"].Value ?? string.Empty : version.Comentarios;
        return string.IsNullOrWhiteSpace(cola) ? fechas : $"{fechas} · {cola}";
    }

    // ----- Validación -----

    private string TextoDeDecision(DecisionValidacionOficial decision) => decision switch
    {
        DecisionValidacionOficial.AutoValidado => TextosDocumento["ValDecisionVerificadoAutomaticamente"],
        DecisionValidacionOficial.RevisionRequerida => Textos["ValDecisionRevision"],
        _ => Textos["ValDecisionSinFirma"],
    };

    private static string Fecha(DateOnly fecha) => fecha.ToString("dd/MM/yyyy");
}
