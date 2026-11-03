using CaeManager.Application.Seguridad.Incidentes.Aviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.ConfirmarYEnviarPrimerAviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.DecidirIncidenteSeguridad;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.EnviarNotificacionCompleta;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Queries.ObtenerIncidenteSeguridad;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.IncidentesSeguridad.Pages;

/// <summary>Borrador editable de un aviso: la página lo rellena con el de la consulta y lo manda al comando.</summary>
public sealed class BorradorAvisoIncidente
{
    public string Naturaleza { get; set; } = "";
    public string DatosEInteresados { get; set; } = "";
    public string Consecuencias { get; set; } = "";
    public string Medidas { get; set; } = "";
    public string Contacto { get; set; } = "";

    public static BorradorAvisoIncidente Desde(ContenidoAvisoIncidente c) => new()
    {
        Naturaleza = c.Naturaleza,
        DatosEInteresados = c.DatosEInteresados,
        Consecuencias = c.Consecuencias,
        Medidas = c.Medidas,
        Contacto = c.Contacto,
    };

    public ContenidoAvisoIncidente AContenido() => new(Naturaleza, DatosEInteresados, Consecuencias, Medidas, Contacto);
}

/// <summary>Paso del aviso que la página ofrece con su único botón primario.</summary>
public enum PasoAviso
{
    Ninguno,
    PrimerAviso,
    NotificacionCompleta,
}

public partial class IncidenteSeguridadPlataforma : CaeManager.Web.Components.PaginaInteractiva
{
    [Parameter] public Guid IncidenteId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private VistaIncidenteSeguridad? _vista;
    private BorradorAvisoIncidente _primerAviso = new(), _completa = new();
    private bool _cargando = true, _procesando, _descartando, _borradoresCargados;
    private string? _error, _errorCarga, _errorMotivo, _errorDescarte;
    private string _motivo = "";
    private Timer? _reloj;

    protected override async Task OnInitializedAsync()
    {
        await CargarAsync();
        // La cuenta atrás se repinta cada minuto; el plazo lo fija ConfirmadoEnUtc, no este reloj.
        _reloj = new Timer(_ => _ = InvokeAsync(StateHasChanged), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Dispose() => _reloj?.Dispose();

    private static string Cuando(DateTime utc) => utc.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm");

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = null;
        try
        {
            var resultado = await Mediator.Send(new ObtenerIncidenteSeguridadQuery(IncidenteId));
            if (resultado.EsFallido)
            {
                _vista = null;
                _errorCarga = resultado.Error.Mensaje;
                return;
            }

            // Los borradores salen de la consulta solo la primera vez que hay vista: recargar tras una acción
            // (evaluar, enviar, descartar) no puede borrar lo que el Actor de Plataforma TALVEG ya ha escrito.
            var primeraVista = !_borradoresCargados;
            _vista = resultado.Valor;
            if (primeraVista)
            {
                _primerAviso = BorradorAvisoIncidente.Desde(_vista.BorradorPrimerAviso);
                _completa = BorradorAvisoIncidente.Desde(_vista.BorradorNotificacionCompleta);
                _borradoresCargados = true;
            }
        }
        catch
        {
            _vista = null;
        }
        finally
        {
            _cargando = false;
        }
    }

    private string Restante(DateTime venceEnUtc, bool enviado)
    {
        if (enviado)
            return Textos["PlazoCumplido"];
        var quedan = venceEnUtc - DateTime.UtcNow;
        return quedan <= TimeSpan.Zero
            ? Textos["PlazoVencido"]
            : Textos["PlazoQuedan", (int)quedan.TotalHours, quedan.Minutes];
    }

    /// <summary>
    /// Todos los destinatarios de todos los Tenants afectados tienen esa plantilla Enviada. Es también lo que
    /// da el plazo por cumplido: con un solo Tenant afectado avisado, la cuenta atrás de los demás sigue, y un
    /// Tenant afectado sin destinatario no ha recibido nada, así que tampoco lo da por cumplido.
    /// </summary>
    private bool TodoEnviado(PlantillaAvisoIncidenteSeguridad plantilla)
    {
        if (_vista is null || _vista.Tenants.Any(t => !t.Destinatarios.Any()))
            return false;
        var esperados = _vista.Tenants.SelectMany(t => t.Destinatarios.Select(d => (t.TenantId, Correo: d.Correo.Trim().ToLowerInvariant()))).ToList();
        if (esperados.Count == 0)
            return false;
        var enviados = _vista.Incidente.Envios
            .Where(e => e.Plantilla == plantilla && e.Estado == EstadoEnvioAvisoIncidenteSeguridad.Enviado)
            .Select(e => (e.TenantAfectadoId, Correo: e.DestinatarioCorreo.Trim().ToLowerInvariant()))
            .ToHashSet();
        return esperados.All(enviados.Contains);
    }

    /// <summary>Repinta la página al editar un apartado: de él dependen los pendientes y el botón de enviar.</summary>
    private static void AlCambiarBorrador() { }

    private List<string> Pendientes()
    {
        var pendientes = new List<string>();
        if (string.IsNullOrWhiteSpace(_completa.Naturaleza)) pendientes.Add(Textos["ApartadoNaturaleza"]);
        if (string.IsNullOrWhiteSpace(_completa.DatosEInteresados)) pendientes.Add(Textos["ApartadoDatosEInteresados"]);
        if (string.IsNullOrWhiteSpace(_completa.Consecuencias)) pendientes.Add(Textos["ApartadoConsecuencias"]);
        if (string.IsNullOrWhiteSpace(_completa.Medidas)) pendientes.Add(Textos["ApartadoMedidas"]);
        if (string.IsNullOrWhiteSpace(_completa.Contacto)) pendientes.Add(Textos["ApartadoContacto"]);
        return pendientes;
    }

    private string NombreEstado(EstadoIncidenteSeguridad estado) => estado switch
    {
        EstadoIncidenteSeguridad.Sospechado => Textos["EstadoSospechado"],
        EstadoIncidenteSeguridad.EnEvaluacion => Textos["EstadoEnEvaluacion"],
        EstadoIncidenteSeguridad.Confirmado => Textos["EstadoConfirmado"],
        _ => Textos["EstadoDescartado"],
    };

    private string NombrePlantilla(PlantillaAvisoIncidenteSeguridad plantilla) =>
        plantilla == PlantillaAvisoIncidenteSeguridad.PrimerAviso ? Textos["PlantillaPrimerAviso"] : Textos["PlantillaNotificacionCompleta"];

    private string DescripcionEnvio(EstadoEnvioAvisoIncidenteSeguridad estado, string plantilla, string nombre, string correo, string quien) =>
        estado switch
        {
            EstadoEnvioAvisoIncidenteSeguridad.Enviado => Textos["EnvioEnviado", plantilla, nombre, correo, quien],
            EstadoEnvioAvisoIncidenteSeguridad.Fallido => Textos["EnvioFallido", plantilla, nombre, correo, quien],
            _ => Textos["EnvioEnviando", plantilla, nombre, correo, quien],
        };

    /// <summary>Sin actor, la transición la hizo la detección automática (el alta del incidente como Sospechado).</summary>
    private string QuienDecidio(Guid? actorUsuarioId) =>
        actorUsuarioId is null ? Textos["PorDeteccionAutomatica"]
        : actorUsuarioId == _vista?.ActorUsuarioId ? Textos["PorTi"]
        : Textos["PorOtroActor"];

    private bool HayIntento(PlantillaAvisoIncidenteSeguridad plantilla) =>
        _vista?.Incidente.Envios.Any(e => e.Plantilla == plantilla) == true;

    /// <summary>
    /// El paso que la página ofrece ahora: el primer aviso mientras el incidente no esté confirmado o ningún
    /// destinatario tenga aún un intento; en cuanto lo haya, la notificación completa, aunque algún primer aviso
    /// haya fallado (un fallido no puede bloquear la completa de todos los Tenants afectados: su reintento queda
    /// como acción secundaria del mismo paso); con la completa enviada a todos (o descartado), ninguno.
    /// </summary>
    private PasoAviso PasoVigente()
    {
        if (_vista is null)
            return PasoAviso.Ninguno;
        return _vista.Incidente.Estado switch
        {
            EstadoIncidenteSeguridad.Sospechado or EstadoIncidenteSeguridad.EnEvaluacion => PasoAviso.PrimerAviso,
            EstadoIncidenteSeguridad.Confirmado when !HayIntento(PlantillaAvisoIncidenteSeguridad.PrimerAviso) => PasoAviso.PrimerAviso,
            EstadoIncidenteSeguridad.Confirmado when !TodoEnviado(PlantillaAvisoIncidenteSeguridad.NotificacionCompleta) => PasoAviso.NotificacionCompleta,
            _ => PasoAviso.Ninguno,
        };
    }

    /// <summary>En el paso de la completa, el primer aviso aún no ha salido a todos: se ofrece reintentarlo.</summary>
    private bool PrimerAvisoPendiente() =>
        _vista?.Incidente.Estado == EstadoIncidenteSeguridad.Confirmado
        && !TodoEnviado(PlantillaAvisoIncidenteSeguridad.PrimerAviso);

    private Task ReintentarPrimerAvisoAsync() =>
        EnviarAsync(() => Mediator.Send(new ConfirmarYEnviarPrimerAvisoCommand(IncidenteId, _primerAviso.AContenido())));

    private string TextoEnviar(PasoAviso paso, EstadoIncidenteSeguridad estado) => paso switch
    {
        PasoAviso.PrimerAviso when estado == EstadoIncidenteSeguridad.Confirmado => Textos["BotonReintentarPrimerAviso"],
        PasoAviso.PrimerAviso => Textos["BotonConfirmarYEnviar"],
        _ => Textos["BotonEnviarCompleta"],
    };

    private Task EnviarPasoVigenteAsync() => PasoVigente() switch
    {
        PasoAviso.PrimerAviso =>
            EnviarAsync(() => Mediator.Send(new ConfirmarYEnviarPrimerAvisoCommand(IncidenteId, _primerAviso.AContenido()))),
        PasoAviso.NotificacionCompleta =>
            EnviarAsync(() => Mediator.Send(new EnviarNotificacionCompletaCommand(IncidenteId, _completa.AContenido()))),
        _ => Task.CompletedTask,
    };

    private async Task EnviarAsync(Func<Task<Result<ResumenEnvioAviso>>> enviar)
    {
        if (_procesando) return;
        _procesando = true;
        _error = null;
        try
        {
            var resultado = await enviar();
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            var resumen = resultado.Valor;
            // Un Tenant afectado sin destinatario no es un éxito: se dice junto al resultado, no solo en la lista.
            var sinDestinatario = resumen.TenantsSinDestinatario.Count > 0
                ? " " + Textos["EnvioSinDestinatario", resumen.TenantsSinDestinatario.Count]
                : "";
            if (resumen.Fallidos > 0 || resumen.EnCurso > 0)
                _error = Textos["EnvioIncompleto", resumen.Enviados + resumen.YaEnviados, resumen.Fallidos, resumen.EnCurso] + sinDestinatario;
            else if (sinDestinatario.Length > 0)
                _error = Textos["EnvioCorrecto", resumen.Enviados + resumen.YaEnviados] + sinDestinatario;
            else
                ToastService.Mostrar(Textos["EnvioCorrecto", resumen.Enviados + resumen.YaEnviados], TonoToast.Exito);
            await CargarAsync();
        }
        catch
        {
            _error = Textos["ErrorEnvio"];
        }
        finally
        {
            _procesando = false;
        }
    }

    private async Task EvaluarAsync()
    {
        if (_procesando) return;
        _procesando = true;
        _error = null;
        try
        {
            var resultado = await Mediator.Send(new DecidirIncidenteSeguridadCommand(IncidenteId, DecisionIncidenteSeguridad.Evaluar, null));
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            await CargarAsync();
        }
        catch
        {
            _error = Textos["ErrorDecidir"];
        }
        finally
        {
            _procesando = false;
        }
    }

    private void AbrirDescarte()
    {
        _descartando = true;
        _motivo = "";
        _errorMotivo = _errorDescarte = _error = null;
    }

    private void CerrarDescarte(bool visible)
    {
        if (visible || _procesando) return;
        _descartando = false;
    }

    private async Task DescartarAsync()
    {
        if (_procesando) return;
        if (string.IsNullOrWhiteSpace(_motivo))
        {
            _errorMotivo = Textos["MotivoObligatorio"];
            return;
        }

        _procesando = true;
        _errorMotivo = _errorDescarte = null;
        try
        {
            var resultado = await Mediator.Send(new DecidirIncidenteSeguridadCommand(IncidenteId, DecisionIncidenteSeguridad.Descartar, _motivo));
            if (resultado.EsFallido)
            {
                _errorDescarte = resultado.Error.Mensaje;
                return;
            }

            _descartando = false;
            ToastService.Mostrar(Textos["DescartadoAviso"], TonoToast.Exito);
            await CargarAsync();
        }
        catch
        {
            _errorDescarte = Textos["ErrorDecidir"];
        }
        finally
        {
            _procesando = false;
        }
    }
}
