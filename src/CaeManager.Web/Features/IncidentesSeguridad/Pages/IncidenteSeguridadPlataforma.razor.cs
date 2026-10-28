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

public partial class IncidenteSeguridadPlataforma : CaeManager.Web.Components.PaginaInteractiva
{
    [Parameter] public Guid IncidenteId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private VistaIncidenteSeguridad? _vista;
    private BorradorAvisoIncidente _primerAviso = new(), _completa = new();
    private bool _cargando = true, _procesando, _descartando;
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

            _vista = resultado.Valor;
            _primerAviso = BorradorAvisoIncidente.Desde(_vista.BorradorPrimerAviso);
            _completa = BorradorAvisoIncidente.Desde(_vista.BorradorNotificacionCompleta);
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

    private bool HayEnviado(PlantillaAvisoIncidenteSeguridad plantilla) =>
        _vista?.Incidente.Envios.Any(e => e.Plantilla == plantilla && e.Estado == EstadoEnvioAvisoIncidenteSeguridad.Enviado) == true;

    /// <summary>Todos los destinatarios de todos los Tenants afectados tienen esa plantilla Enviada.</summary>
    private bool TodoEnviado(PlantillaAvisoIncidenteSeguridad plantilla)
    {
        if (_vista is null)
            return false;
        var esperados = _vista.Tenants.SelectMany(t => t.Destinatarios.Select(d => (t.TenantId, Correo: d.Correo.Trim().ToLowerInvariant()))).ToList();
        if (esperados.Count == 0)
            return false;
        var enviados = _vista.Incidente.Envios
            .Where(e => e.Plantilla == plantilla && e.Estado == EstadoEnvioAvisoIncidenteSeguridad.Enviado)
            .Select(e => (e.TenantAfectadoId, Correo: e.DestinatarioCorreo))
            .ToHashSet();
        return esperados.All(enviados.Contains);
    }

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

    private string QuienDecidio(Guid? actorUsuarioId) =>
        actorUsuarioId == _vista?.ActorUsuarioId ? Textos["PorTi"] : Textos["PorOtroActor"];

    private Task ConfirmarYEnviarAsync() =>
        EnviarAsync(() => Mediator.Send(new ConfirmarYEnviarPrimerAvisoCommand(IncidenteId, _primerAviso.AContenido())));

    private Task EnviarCompletaAsync() =>
        EnviarAsync(() => Mediator.Send(new EnviarNotificacionCompletaCommand(IncidenteId, _completa.AContenido())));

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
            if (resumen.Fallidos > 0 || resumen.EnCurso > 0)
                _error = Textos["EnvioIncompleto", resumen.Enviados + resumen.YaEnviados, resumen.Fallidos, resumen.EnCurso];
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
