using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.Notificaciones;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.DocumentosIa;

/// <summary>
/// Consume <see cref="ColaAnalisisDocumentoEnMemoria"/> y ejecuta los
/// análisis fuera del circuito de Blazor, avisando al usuario por la campana
/// (<see cref="NotificacionUsuario"/>) cuando terminan.
///
/// Tres cosas que hacen falta aquí y no en el Command:
///
/// 1. Un <b>scope de DI propio</b> por encargo. Este servicio es singleton,
///    como todo <c>BackgroundService</c>, y los servicios que necesita
///    (DbContext, repositorios) son scoped.
/// 2. <b>Restablecer el tenant</b> con <c>AmbitoTenantExplicito</c>. Fuera del
///    circuito no hay claim de sesión, así que <c>ITenantActual</c> resolvería
///    a null y el filtro global no encontraría el documento — fallo cerrado,
///    correcto pero inútil aquí. Es el modo previsto para procesos de fondo
///    (docs/MULTITENANCY.md § 8.4).
/// 3. <b>Aislar cada encargo</b>. Un fallo procesando un documento no puede
///    tumbar el bucle y dejar sin procesar todo lo que venga detrás.
/// </summary>
public class ProcesadorAnalisisDocumentoHostedService(
    ColaAnalisisDocumentoEnMemoria cola,
    IServiceScopeFactory ambitoFactory,
    ILogger<ProcesadorAnalisisDocumentoHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var encargo in cola.Lector.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcesarAsync(encargo, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Apagado normal de la aplicación, no es un error.
                break;
            }
            catch (Exception ex)
            {
                // Mismo criterio de "mejor esfuerzo" que tenía el Command: el
                // análisis puede fallar sin que eso invalide el documento ya
                // subido. Lo que no puede es pasar en silencio.
                logger.LogError(ex,
                    "Falló el análisis {Tipo} del documento {DocumentoId} (tenant {TenantId}).",
                    encargo.Tipo, encargo.DocumentoId, encargo.TenantId);
            }
        }
    }

    private async Task ProcesarAsync(EncargoAnalisisDocumento encargo, CancellationToken cancellationToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(encargo.TenantId);

        switch (encargo.Tipo)
        {
            case TipoAnalisisDocumento.VerificacionIa:
                await ambito.ServiceProvider.GetRequiredService<IVerificacionIaDocumentoService>()
                    .ProcesarDocumentoAsync(encargo.DocumentoId, cancellationToken);
                break;

            case TipoAnalisisDocumento.DeteccionTrabajadores:
                await ambito.ServiceProvider.GetRequiredService<IDeteccionTrabajadoresService>()
                    .ProcesarDocumentoAsync(encargo.DocumentoId, cancellationToken);
                break;
        }

        await AvisarAsync(ambito.ServiceProvider, encargo, cancellationToken);
    }

    /// <summary>
    /// La campana, no un correo: el usuario que acaba de subir el documento
    /// suele seguir en la aplicación, y <see cref="NotificacionUsuario"/>
    /// sobrevive a recargas y a cerrar sesión, así que tampoco se pierde si
    /// no lo está.
    /// </summary>
    private async Task AvisarAsync(
        IServiceProvider servicios, EncargoAnalisisDocumento encargo, CancellationToken cancellationToken)
    {
        if (encargo.UsuarioSolicitanteId is not { } usuarioId) return;

        var titulo = encargo.Tipo == TipoAnalisisDocumento.VerificacionIa
            ? "Verificación automática terminada"
            : "Detección de personal terminada";

        var mensaje = encargo.Tipo == TipoAnalisisDocumento.VerificacionIa
            ? "Ya está revisado el documento que subiste. Comprueba el resultado por si necesita tu confirmación."
            : "Ya se ha analizado el documento que subiste en busca de altas y bajas de personal.";

        var urlAccion = encargo.Tipo == TipoAnalisisDocumento.VerificacionIa ? "/documentos/revision-ia" : "/trabajadores";
        var textoAccion = encargo.Tipo == TipoAnalisisDocumento.VerificacionIa ? "Ver revisión" : "Ver trabajadores";

        servicios.GetRequiredService<INotificacionUsuarioRepository>()
            .Agregar(new NotificacionUsuario(usuarioId, titulo, mensaje, urlAccion, textoAccion));

        await servicios.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);
    }
}
