using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Notificaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Comunicaciones.Deteccion;

public class AlertasCriticasOptions
{
    public const string Seccion = "AlertasCriticas";
    /// <summary>Clave: Tenant propietario del buzón, que es el Operador CAE. Una lista vacía desactiva las reglas de ese Tenant.</summary>
    public Dictionary<Guid, string[]> ExpresionesPorOperadorCae { get; set; } = [];
}

public record SegundaOpinionCritica(bool EsCritica, int Confianza);

public interface ISegundaOpinionCriticaIa
{
    Task<SegundaOpinionCritica?> EvaluarAsync(string texto, CancellationToken cancellationToken);
}

public interface IAlertaCriticaRepository
{
    Task<AlertaCriticaConversacion?> ObtenerPorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken);
    void Agregar(AlertaCriticaConversacion alerta);
}

public interface IAlertaCriticaService
{
    Task ProcesarAsync(Mensaje mensaje, CancellationToken cancellationToken);
}

/// <summary>Se incorpora a la misma unidad de trabajo de la ingesta. Solo propone; no confirma ni envía correo.</summary>
public class AlertaCriticaService(
    IAlertaCriticaRepository repositorio,
    INotificacionUsuarioRepository notificaciones,
    IDirectorioUsuariosService directorio,
    ITenantActual tenantActual,
    IOptionsMonitor<AlertasCriticasOptions> opciones,
    ISegundaOpinionCriticaIa segundaOpinion,
    CaeManager.Application.Cumplimiento.IInstruccionTratamientoIaService instruccionTratamientoIa,
    ILogger<AlertaCriticaService> logger) : IAlertaCriticaService
{
    // Nunca rompe la ingesta: un fallo del detector, del directorio o del repositorio se registra y el mensaje entra igual.
    public async Task ProcesarAsync(Mensaje mensaje, CancellationToken cancellationToken)
    {
        try { await ProcesarSinAislarAsync(mensaje, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "La detección de alerta crítica del mensaje {MensajeId} falló; la ingesta sigue.", mensaje.Id);
        }
    }

    private async Task ProcesarSinAislarAsync(Mensaje mensaje, CancellationToken cancellationToken)
    {
        // Un autorrespondedor o un aviso de plataforma no es un caso crítico aunque cite expresiones legales.
        if (mensaje.Direccion != DireccionMensaje.Entrante || mensaje.EsAutomatico || tenantActual.TenantId is not { } tenantId) return;
        var reglas = opciones.CurrentValue.ExpresionesPorOperadorCae.GetValueOrDefault(tenantId);
        var coincidencia = DetectorExpresionesCriticas.Detectar(mensaje, reglas);
        if (coincidencia is null || await repositorio.ObtenerPorConversacionAsync(mensaje.ConversacionId, cancellationToken) is not null) return;

        var alerta = new AlertaCriticaConversacion(mensaje, coincidencia.Texto, coincidencia.Expresion, tenantId, DateTime.UtcNow);
        // La indisponibilidad de la segunda opinión nunca impide la alerta determinista. Sin Nivel 0
        // el texto no sale al proveedor de IA.
        try
        {
            if (await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken)
                && await segundaOpinion.EvaluarAsync(coincidencia.Texto, cancellationToken) is { Confianza: >= 0 and <= 100 } opinion)
                alerta.AnotarSegundaOpinion(opinion.EsCritica, opinion.Confianza);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segunda opinión de alerta crítica no disponible.");
        }

        repositorio.Agregar(alerta);
        await AvisarAsync(alerta, false, cancellationToken);
    }

    public async Task AvisarAsync(AlertaCriticaConversacion alerta, bool confirmada, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId || alerta.OperadorCaeTenantId != tenantId)
            throw new InvalidOperationException("La alerta debe permanecer en el Tenant propietario del buzón.");
        var destinatarios = new HashSet<Guid>();
        foreach (var rol in new[] { "DireccionCae", "CoordinadorCae" })
            destinatarios.UnionWith(await directorio.ObtenerCuentasActivasConRolAsync(tenantId, rol, cancellationToken));
        foreach (var persona in destinatarios)
            notificaciones.Agregar(new NotificacionUsuario(persona,
                confirmada ? "Caso crítico confirmado y derivado" : "Posible caso crítico",
                confirmada ? "La Dirección CAE o el Coordinador CAE ha confirmado el caso para su atención." : "Hay una conversación que requiere revisión de la Dirección CAE o del Coordinador CAE.",
                $"/comunicaciones?conversacion={alerta.ConversacionId}", "Revisar conversación"));
    }
}
