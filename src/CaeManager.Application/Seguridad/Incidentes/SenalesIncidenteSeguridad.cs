using CaeManager.Domain.Plataforma;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Seguridad.Incidentes;

/// <inheritdoc cref="ISenalesIncidenteSeguridad"/>
/// <remarks>
/// Singleton: los contadores de ventana viven aquí. Registrar el incidente va por
/// <see cref="IRegistroIncidentesSeguridad"/>; solo un incidente <b>nuevo</b> encola la
/// alerta (una repetición dentro de la ventana de deduplicación no vuelve a avisar).
/// </remarks>
public sealed class SenalesIncidenteSeguridad(
    IRegistroIncidentesSeguridad registro,
    IAlertaIncidenteSeguridad alerta,
    IOptions<OpcionesIncidentesSeguridad> opciones,
    TimeProvider reloj,
    ILogger<SenalesIncidenteSeguridad> logger) : ISenalesIncidenteSeguridad
{
    private readonly VentanaDeslizantePorSujeto _filasExportadas = new();
    private readonly VentanaDeslizantePorSujeto _documentosSensibles = new();
    private readonly VentanaDeslizantePorSujeto _rechazosOtroTenant = new();

    public static string SujetoUsuario(Guid usuarioId) => $"usuario:{usuarioId:D}";

    public Task InicioSesionNoReconocidoAsync(Guid usuarioId, Guid tenantPropietarioId, bool teniaContrasena, CancellationToken ct = default)
        => RegistrarAsync(
            SenalIncidenteSeguridad.InicioSesionNoReconocido, SujetoUsuario(usuarioId), [tenantPropietarioId],
            new EvidenciaIncidenteSeguridad()
                .Con("usuario_id", usuarioId)
                .Con("tenant_propietario_cuenta_id", tenantPropietarioId)
                .Con("cuenta_con_contrasena_local", teniaContrasena)
                .Con("confirmado_en_utc", reloj.GetUtcNow().UtcDateTime),
            ct);

    public Task ExportacionAsync(Guid actorRealUsuarioId, Guid? tenantId, int filas, CancellationToken ct = default)
        => ExtraccionAsync(_filasExportadas, "filas_exportadas_en_ventana", opciones.Value.UmbralFilasExportadas,
            actorRealUsuarioId, tenantId, filas, ct);

    public Task DocumentoSensibleAbiertoAsync(Guid actorRealUsuarioId, Guid? tenantId, CancellationToken ct = default)
        => ExtraccionAsync(_documentosSensibles, "documentos_sensibles_en_ventana", opciones.Value.UmbralDocumentosSensibles,
            actorRealUsuarioId, tenantId, 1, ct);

    public Task AccesoAOtroTenantRechazadoAsync(Guid usuarioId, Guid tenantOrigenId, Guid tenantObjetivoId, CancellationToken ct = default)
    {
        var o = opciones.Value;
        var sujeto = SujetoUsuario(usuarioId);
        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (!_rechazosOtroTenant.Sumar(sujeto, 1, o.UmbralRechazosOtroTenant, o.VentanaRechazosOtroTenant, ahora, tenantObjetivoId))
            return Task.CompletedTask;

        var objetivos = _rechazosOtroTenant.EtiquetasEnVentana(sujeto);
        return RegistrarAsync(
            SenalIncidenteSeguridad.AccesoRepetidoAOtroTenant, sujeto, objetivos,
            new EvidenciaIncidenteSeguridad()
                .Con("usuario_id", usuarioId)
                .Con("tenant_origen_id", tenantOrigenId)
                .Con("tenants_objetivo_ids", objetivos)
                .Con("rechazos_en_ventana", _rechazosOtroTenant.SumaActual(sujeto))
                .Con("minutos_ventana", (long)o.VentanaRechazosOtroTenant.TotalMinutes)
                .Con("umbral", (long)o.UmbralRechazosOtroTenant),
            ct);
    }

    private Task ExtraccionAsync(
        VentanaDeslizantePorSujeto ventana, string claveRecuento, int umbral,
        Guid actorRealUsuarioId, Guid? tenantId, int peso, CancellationToken ct)
    {
        var o = opciones.Value;
        var sujeto = SujetoUsuario(actorRealUsuarioId);
        if (!ventana.Sumar(sujeto, peso, umbral, o.VentanaExtraccion, reloj.GetUtcNow().UtcDateTime, tenantId))
            return Task.CompletedTask;

        var tenants = ventana.EtiquetasEnVentana(sujeto);
        return RegistrarAsync(
            SenalIncidenteSeguridad.ExtraccionMasiva, sujeto, tenants,
            new EvidenciaIncidenteSeguridad()
                .Con("usuario_id", actorRealUsuarioId)
                .Con(claveRecuento, ventana.SumaActual(sujeto))
                .Con("minutos_ventana", (long)o.VentanaExtraccion.TotalMinutes)
                .Con("umbral", (long)umbral),
            ct);
    }

    private async Task RegistrarAsync(
        SenalIncidenteSeguridad senal, string sujeto, IReadOnlyCollection<Guid> tenants,
        EvidenciaIncidenteSeguridad evidencia, CancellationToken ct)
    {
        try
        {
            // Sin el token de la petición: el contador ya marcó la ventana como avisada,
            // y abortar la petición no puede servir para que la señal se pierda.
            _ = ct;
            var resultado = await registro.RegistrarAsync(
                senal, sujeto, tenants, evidencia, opciones.Value.VentanaDeduplicacion, CancellationToken.None);
            if (resultado.EsNuevo)
            {
                logger.LogWarning("Incidente de seguridad sospechado {IncidenteId} abierto por la señal {Senal}.", resultado.IncidenteId, senal);
                alerta.Encolar(resultado.IncidenteId);
            }
            else
            {
                logger.LogInformation("La señal {Senal} se agrupa en el incidente de seguridad abierto {IncidenteId}.", senal, resultado.IncidenteId);
            }
        }
        catch (Exception ex)
        {
            // Detectar no puede romper la operación que disparó la señal. El error
            // llega a Sentry por el log, que es el segundo camino hacia la guardia.
            logger.LogError(ex, "No se pudo registrar el incidente de seguridad de la señal {Senal}.", senal);
        }
    }
}
