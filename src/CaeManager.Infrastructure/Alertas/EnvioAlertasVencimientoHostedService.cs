using System.Net;
using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Alertas;

/// <summary>
/// Resumen diario por correo de las alertas de vencimiento (Issue #2) — el
/// valor real detrás del email de bienvenida, según el propio Issue: "el
/// valor real de negocio está en alertas proactivas de vencimiento de
/// Documentos por correo, no solo el email de bienvenida".
///
/// Envuelto en elección de líder (P3-30), igual que
/// <see cref="Integraciones.RenovacionSuscripcionWebhookHostedService"/> —
/// mismo motivo: dos réplicas no deben mandar el mismo resumen duplicado.
///
/// Destinatarios: Administrador y DireccionCae del tenant — roles con
/// visión total ya establecida (<c>RolesDeAdministracionAmpliada</c>), no
/// una cartera por Gestor CAE. Atribuir una alerta a "el" Gestor responsable
/// exigiría resolver Trabajador → Empresa/Subcontrata → Cliente, y una
/// Empresa/Subcontrata puede prestar servicio a varios Clientes a la vez
/// (EmpresaCliente/SubcontrataCliente son N:M, ver Fase 31) — un Documento
/// compartido no tiene un único "responsable de cartera" sin una decisión de
/// producto que no se ha tomado todavía. Acotado a los roles globales por
/// ahora; el resumen por Gestor CAE queda en ROADMAP.md como extensión futura.
///
/// Cálculo de alertas reutilizado de <see cref="ObtenerAlertasQueryHandler.CalcularAsync"/>
/// sin pasar por <see cref="IAlcanceDatosService"/> (que depende del usuario
/// de la sesión actual, inexistente en un job de fondo) — <c>null, null</c>
/// pide el cálculo sin restricción de cartera dentro del tenant ya acotado
/// por <see cref="AmbitoTenantExplicito"/>.
/// </summary>
public class EnvioAlertasVencimientoHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    IOptions<AlertasPorCorreoOptions> opciones,
    ILogger<EnvioAlertasVencimientoHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await EnviarATodosLosTenantsAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await EnviarATodosLosTenantsAsync(stoppingToken);
    }

    private async Task EnviarATodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("envio-alertas-vencimiento", ProcesarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de envío del resumen de alertas de vencimiento por correo.");
        }
    }

    private async Task ProcesarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenantsActivos;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenantsActivos = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenantsActivos)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado igual que ProcesadorAnalisisDocumentoHostedService: sin
            // este try/catch, un tenant sin destinatarios válidos o con un
            // fallo persistente de IEmailService abortaba este foreach
            // entero y dejaba sin resumen diario a los tenants siguientes.
            try
            {
                await ProcesarTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el envío del resumen de alertas de vencimiento del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var registroAutomatizaciones = ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
        if (!await registroAutomatizaciones.EstaActivoAsync(CatalogoAutomatizaciones.AlertasVencimientoDiarias, stoppingToken))
            return;

        try
        {
            // Éxito solo si TODOS los destinatarios recibieron el resumen —
            // antes, un fallo de envío individual solo dejaba un warning en
            // el log y el ciclo se registraba "exitosa" igual, ocultando el
            // fallo en la pantalla de Automatizaciones.
            var (huboFallosDeEnvio, alertasEvaluadas) = await EjecutarEnvioAsync(ambito, tenantId, stoppingToken);
            await registroAutomatizaciones.RegistrarEjecucionAsync(
                CatalogoAutomatizaciones.AlertasVencimientoDiarias, exitosa: !huboFallosDeEnvio, stoppingToken,
                elementosEvaluados: alertasEvaluadas);
        }
        catch (Exception ex)
        {
            await registroAutomatizaciones.RegistrarEjecucionAsync(
                CatalogoAutomatizaciones.AlertasVencimientoDiarias, exitosa: false, stoppingToken, mensajeError: ex.Message);
            throw;
        }
    }

    /// <returns>Si al menos un destinatario no recibió el correo, y cuántas alertas se evaluaron.</returns>
    private async Task<(bool HuboFallos, int AlertasEvaluadas)> EjecutarEnvioAsync(IServiceScope ambito, Guid tenantId, CancellationToken stoppingToken)
    {
        var dbContext = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var alcanceDatos = ambito.ServiceProvider.GetRequiredService<IAlcanceDatosService>();
        var documentosFaltantesService = ambito.ServiceProvider.GetRequiredService<IDocumentosFaltantesService>();
        var resolverClientePrincipal = ambito.ServiceProvider.GetRequiredService<IResolverClientePrincipalService>();
        // Instanciado directo, sin pasar por MediatR: CalcularAsync es el
        // punto de entrada explícito pensado exactamente para esto (ver su
        // comentario) — MediatR no garantiza que el tipo concreto del
        // handler sea resoluble por sí mismo desde el contenedor.
        var handler = new ObtenerAlertasQueryHandler(
            dbContext, dbContext, dbContext, dbContext, dbContext, dbContext, dbContext, resolverClientePrincipal, alcanceDatos, documentosFaltantesService);

        var alertas = await handler.CalcularAsync(trabajadorIdsVisibles: null, centroIdsVisibles: null, stoppingToken);
        if (alertas.Count == 0) return (false, 0);

        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();
        var administradores = await directorio.ObtenerVisiblesEnRolAsync(Roles.Administrador, stoppingToken);
        var direccionCae = await directorio.ObtenerVisiblesEnRolAsync(Roles.DireccionCae, stoppingToken);

        var destinatarios = administradores.Concat(direccionCae)
            .Where(u => !string.IsNullOrWhiteSpace(u.Email))
            .DistinctBy(u => u.Id)
            .ToList();

        if (destinatarios.Count == 0) return (false, alertas.Count);

        var emailService = ambito.ServiceProvider.GetRequiredService<IEmailService>();
        var (asunto, cuerpo) = ConstruirCorreo(alertas, opciones.Value.UrlBase);

        var huboFallos = false;
        foreach (var destinatario in destinatarios)
        {
            var resultado = await emailService.EnviarAsync(destinatario.Email!, asunto, cuerpo, stoppingToken);
            if (resultado.EsFallido)
            {
                huboFallos = true;
                logger.LogWarning(
                    "No se pudo enviar el resumen de alertas de vencimiento a {Email} (tenant {TenantId}): {Error}",
                    destinatario.Email, tenantId, resultado.Error.Mensaje);
            }
        }

        return (huboFallos, alertas.Count);
    }

    private static (string Asunto, string CuerpoHtml) ConstruirCorreo(IReadOnlyList<AlertaDto> alertas, string? urlBase)
    {
        var vencidos = alertas.Count(a => a.Estado == EstadoDocumento.Vencido);
        var urgentes = alertas.Count(a => a.Estado == EstadoDocumento.Urgente);
        var proximos = alertas.Count(a => a.Estado == EstadoDocumento.Proximo);
        var faltantes = alertas.Count(a => a.Estado == EstadoDocumento.Faltante);

        var asunto = $"{Marca.Nombre} — {alertas.Count} alerta(s) de documentación pendientes de revisión";

        var enlace = string.IsNullOrWhiteSpace(urlBase)
            ? string.Empty
            : $"""<p><a href="{WebUtility.HtmlEncode(urlBase.TrimEnd('/'))}/alertas">Ver el detalle en Alertas</a></p>""";

        var cuerpo = $"""
            <p>Resumen diario de documentación pendiente en {Marca.Nombre}:</p>
            <ul>
                <li><strong>{vencidos}</strong> documento(s) vencido(s)</li>
                <li><strong>{urgentes}</strong> próximo(s) a vencer (urgente)</li>
                <li><strong>{proximos}</strong> próximo(s) a vencer</li>
                <li><strong>{faltantes}</strong> documento(s) obligatorio(s) sin subir</li>
            </ul>
            {enlace}
            """;

        return (asunto, cuerpo);
    }
}
