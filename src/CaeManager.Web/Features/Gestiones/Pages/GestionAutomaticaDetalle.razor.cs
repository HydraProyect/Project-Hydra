using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
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
    private bool _guardando;
    private string? _errorGuardado;

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
            ? Textos["PasoValidadoHasta", hasta.ToString("dd/MM/yyyy")].Value
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

    private bool PuedeGuardar => _opcion switch
    {
        OpcionSubido => true,
        OpcionValidado => _hasta is not null || _noSe,
        OpcionRechazado => !string.IsNullOrEmpty(_causa) && !string.IsNullOrWhiteSpace(_motivo),
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
        _errorGuardado = null;
        _menuAbierto = false;
    }

    private void ElegirOpcion(string clave) => _opcion = clave;

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
                default:
                    return;
            }

            if (resultado.EsFallido)
            {
                _errorGuardado = resultado.Error.Mensaje;
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
