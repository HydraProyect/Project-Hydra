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
            SenalIncidenteSeguridad.InicioSesionNoReconocido, SujetoUsuario(usuarioId),
            () => ([tenantPropietarioId], new EvidenciaIncidenteSeguridad()
                .Con("usuario_id", usuarioId)
                .Con("tenant_propietario_cuenta_id", tenantPropietarioId)
                .Con("cuenta_con_contrasena_local", teniaContrasena)
                .Con("confirmado_en_utc", reloj.GetUtcNow().UtcDateTime)),
            alFallar: null);

    public Task ExportacionAsync(Guid actorRealUsuarioId, Guid? tenantId, int filas, CancellationToken ct = default)
        => ExtraccionAsync(_filasExportadas, "filas_exportadas_en_ventana", opciones.Value.UmbralFilasExportadas,
            actorRealUsuarioId, tenantId, filas);

    public Task DocumentoSensibleAbiertoAsync(Guid actorRealUsuarioId, Guid? tenantId, CancellationToken ct = default)
        => ExtraccionAsync(_documentosSensibles, "documentos_sensibles_en_ventana", opciones.Value.UmbralDocumentosSensibles,
            actorRealUsuarioId, tenantId, 1);

    /// <remarks>
    /// El Tenant afectado es el <b>de origen</b> del usuario, el único que se sabe
    /// comprometido (es su cuenta la que fuerza la petición). Los Tenants pedidos los
    /// elige quien envía la petición, así que solo van a la evidencia: si fueran los
    /// afectados, cualquiera podría hacer que un aviso de violación apuntara a Tenants
    /// ajenos con solo pedirlos.
    /// </remarks>
    public Task AccesoAOtroTenantRechazadoAsync(Guid usuarioId, Guid tenantOrigenId, Guid tenantObjetivoId, CancellationToken ct = default)
    {
        var o = opciones.Value;
        var sujeto = SujetoUsuario(usuarioId);
        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (!_rechazosOtroTenant.Sumar(sujeto, 1, o.UmbralRechazosOtroTenant, o.VentanaRechazosOtroTenant, ahora, tenantObjetivoId))
            return Task.CompletedTask;

        return RegistrarAsync(
            SenalIncidenteSeguridad.AccesoRepetidoAOtroTenant, sujeto,
            () =>
            {
                var objetivos = _rechazosOtroTenant.EtiquetasEnVentana(sujeto);
                var evidencia = new EvidenciaIncidenteSeguridad()
                    .Con("usuario_id", usuarioId)
                    .Con("tenant_origen_id", tenantOrigenId)
                    .Con("rechazos_en_ventana", _rechazosOtroTenant.SumaActual(sujeto))
                    .Con("minutos_ventana", (long)o.VentanaRechazosOtroTenant.TotalMinutes)
                    .Con("umbral", (long)o.UmbralRechazosOtroTenant);
                return ([tenantOrigenId], ConLista(evidencia, "tenants_objetivo_ids", objetivos));
            },
            alFallar: () => _rechazosOtroTenant.Desmarcar(sujeto, ReintentarDesde()));
    }

    private Task ExtraccionAsync(
        VentanaDeslizantePorSujeto ventana, string claveRecuento, int umbral,
        Guid actorRealUsuarioId, Guid? tenantId, int peso)
    {
        var o = opciones.Value;
        var sujeto = SujetoUsuario(actorRealUsuarioId);
        if (!ventana.Sumar(sujeto, peso, umbral, o.VentanaExtraccion, reloj.GetUtcNow().UtcDateTime, tenantId))
            return Task.CompletedTask;

        return RegistrarAsync(
            SenalIncidenteSeguridad.ExtraccionMasiva, sujeto,
            () => (ventana.EtiquetasEnVentana(sujeto), new EvidenciaIncidenteSeguridad()
                .Con("usuario_id", actorRealUsuarioId)
                .Con(claveRecuento, ventana.SumaActual(sujeto))
                .Con("minutos_ventana", (long)o.VentanaExtraccion.TotalMinutes)
                .Con("umbral", (long)umbral)),
            alFallar: () => ventana.Desmarcar(sujeto, ReintentarDesde()));
    }

    /// <summary>Enfriamiento tras un registro fallido antes de reintentarlo.</summary>
    public static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromSeconds(30);

    private DateTime ReintentarDesde() => reloj.GetUtcNow().UtcDateTime + EsperaTrasFallo;

    /// <summary>
    /// Pone una lista de Tenants en la evidencia sin pasar del tope de la función de
    /// registro: si hay más, guarda los primeros y el total.
    /// </summary>
    private static EvidenciaIncidenteSeguridad ConLista(EvidenciaIncidenteSeguridad evidencia, string clave, IReadOnlyCollection<Guid> valores)
    {
        evidencia.Con(clave, valores.Take(IncidenteSeguridadSospechado.MaximoTenantsAfectados));
        if (valores.Count > IncidenteSeguridadSospechado.MaximoTenantsAfectados)
            evidencia.Con(clave + "_total", (long)valores.Count);
        return evidencia;
    }

    /// <param name="construir">
    /// Tenants afectados y evidencia, construidos dentro del <c>try</c>: un fallo al
    /// armarlos tampoco puede romper la operación que disparó la señal.
    /// </param>
    /// <param name="alFallar">
    /// Qué hacer si el registro falla: desmarcar la ventana, para que la operación
    /// siguiente vuelva a intentarlo en vez de perder la señal hasta que la ventana pase.
    /// </param>
    private async Task RegistrarAsync(
        SenalIncidenteSeguridad senal, string sujeto,
        Func<(IReadOnlyCollection<Guid> Tenants, EvidenciaIncidenteSeguridad Evidencia)> construir,
        Action? alFallar)
    {
        IncidenteRegistrado resultado;
        try
        {
            var (tenants, evidencia) = construir();
            if (tenants.Count > IncidenteSeguridadSospechado.MaximoTenantsAfectados)
            {
                evidencia.Con("tenants_afectados_total", (long)tenants.Count);
                tenants = tenants.Take(IncidenteSeguridadSospechado.MaximoTenantsAfectados).ToList();
            }

            // Sin el token de la petición: abortar la petición no puede servir para que
            // la señal se pierda.
            resultado = await registro.RegistrarAsync(
                senal, sujeto, tenants, evidencia, opciones.Value.VentanaDeduplicacion, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Detectar no puede romper la operación que disparó la señal. El error
            // llega a Sentry por el log, que es el segundo camino hacia la guardia.
            alFallar?.Invoke();
            logger.LogError(ex, "No se pudo registrar el incidente de seguridad de la señal {Senal}.", senal);
            return;
        }

        // Fuera del try: el incidente ya está registrado, y desmarcar la ventana por un
        // fallo de aquí haría que el reintento lo agrupara sin volver a encolar la alerta.
        // Encolar no lanza (contrato de IAlertaIncidenteSeguridad).
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
}
