using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso;

/// <summary>
/// Quién puede decidir sobre un incidente y enviar su aviso, antes de llegar a la base: un Actor de Plataforma
/// TALVEG con concesión AdminPlataforma global vigente, desde su propia cuenta (fuera de una Sesión Privilegiada)
/// y con el segundo factor activo y superado en esta sesión (diseño del aviso: «Actor de Plataforma TALVEG, con
/// segundo factor»; mismo criterio que abrir una Sesión Privilegiada). Las funciones de la base repiten la
/// concesión y la cuenta de plataforma; el segundo factor solo se comprueba aquí.
/// </summary>
internal static class AutorizacionAvisoIncidenteSeguridad
{
    /// <summary>Puede leer el incidente: la concesión, fuera de una Sesión Privilegiada.</summary>
    public static async Task<Guid?> ActorQueLeeAsync(
        ICurrentUserService currentUserService,
        ISesionPrivilegiadaActual sesionPrivilegiadaActual,
        IAutorizacionAdminPlataforma autorizacion,
        CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null)
            return null;

        return await currentUserService.ObtenerUsuarioActualIdAsync() is { } usuarioId
               && await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken)
            ? usuarioId
            : null;
    }

    /// <summary>Puede decidir y enviar: además, el segundo factor. Null si puede; si no, el motivo.</summary>
    public static async Task<Error?> MotivoParaNoDecidirAsync(
        ICurrentUserService currentUserService,
        ISesionPrivilegiadaActual sesionPrivilegiadaActual,
        IAutorizacionAdminPlataforma autorizacion,
        CancellationToken cancellationToken)
    {
        if (await ActorQueLeeAsync(currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken) is null)
            return ErroresIncidenteSeguridad.SoloActorDePlataforma;
        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return ErroresIncidenteSeguridad.SinDobleFactor;
        if (!await currentUserService.SuperoTotpEnSesionAsync())
            return ErroresIncidenteSeguridad.TotpNoVerificado;
        return null;
    }

    /// <summary>Valida los apartados del aviso: todos con texto (la completa) o los del primer aviso.</summary>
    public static Error? ValidarContenido(ContenidoAvisoIncidente? contenido, PlantillaAvisoIncidenteSeguridad plantilla)
    {
        if (contenido is null)
            return ErroresIncidenteSeguridad.ContenidoIncompleto;
        if (contenido.Apartados.Any(a => (a?.Length ?? 0) > ContenidoAvisoIncidente.LongitudMaximaApartado))
            return ErroresIncidenteSeguridad.ContenidoDemasiadoLargo;

        IEnumerable<string?> obligatorios = plantilla == PlantillaAvisoIncidenteSeguridad.NotificacionCompleta
            ? contenido.Apartados
            : [contenido.Naturaleza, contenido.DatosEInteresados, contenido.Medidas, contenido.Contacto];
        return obligatorios.Any(string.IsNullOrWhiteSpace) ? ErroresIncidenteSeguridad.ContenidoIncompleto : null;
    }
}

/// <summary>
/// Envía una plantilla del aviso a todos los destinatarios de los Tenants afectados, una fila por destinatario.
/// Idempotente: lo ya enviado no se repite, lo fallido se reintenta con el asunto y el cuerpo guardados, y lo
/// que otro clic está enviando se deja. Un fallo del correo, o un destinatario que la base rechaza por sus
/// datos, no aborta a los demás destinatarios; solo cortan el envío los errores que valen para todos (sin
/// autorización, incidente no encontrado o no confirmado).
/// </summary>
internal static class EnvioAvisosIncidenteSeguridad
{
    /// <summary>Un envío preparado: el correo que saldría hacia un destinatario de un Tenant afectado.</summary>
    private sealed record EnvioPreparado(Guid TenantId, string Correo, EnvioAvisoSolicitado Solicitado);

    /// <summary>
    /// Comprueba, antes de confirmar el incidente, que ningún correo resultante (con los apartados ya escapados
    /// para HTML y el saludo de cada destinatario) supera la longitud que la base admite. La hora de confirmación
    /// solo cambia el texto de la fecha, que tiene siempre la misma longitud.
    /// </summary>
    public static async Task<Error?> ValidarLongitudAsync(
        IIncidentesSeguridadPlataforma plataforma,
        IncidenteSeguridadDto incidente,
        DateTime confirmadoEnUtc,
        PlantillaAvisoIncidenteSeguridad plantilla,
        ContenidoAvisoIncidente contenido,
        IReadOnlyList<DestinatariosAvisoSeguridadTenant> destinatarios,
        CancellationToken cancellationToken)
    {
        var (preparados, _) = await PrepararAsync(
            plataforma, incidente, confirmadoEnUtc, plantilla, contenido, destinatarios, cancellationToken);
        return preparados.Any(p => DemasiadoLargo(p.Solicitado)) ? ErroresIncidenteSeguridad.CuerpoDemasiadoLargo : null;
    }

    private static bool DemasiadoLargo(EnvioAvisoSolicitado solicitado) =>
        solicitado.Cuerpo.Length > EnvioAvisoIncidenteSeguridad.LongitudMaximaCuerpo
        || solicitado.Asunto.Length > EnvioAvisoIncidenteSeguridad.LongitudMaximaAsunto;

    /// <summary>Errores de la base que solo afectan a ese destinatario: se anotan y se sigue con los demás.</summary>
    private static bool EsFalloDelDestinatario(Error error) =>
        error.Codigo == ErroresIncidenteSeguridad.DestinatarioNoValido.Codigo
        || error.Codigo == ErroresIncidenteSeguridad.ContenidoDemasiadoLargo.Codigo;

    private static async Task<(List<EnvioPreparado> Preparados, List<Guid> SinDestinatario)> PrepararAsync(
        IIncidentesSeguridadPlataforma plataforma,
        IncidenteSeguridadDto incidente,
        DateTime confirmadoEnUtc,
        PlantillaAvisoIncidenteSeguridad plantilla,
        ContenidoAvisoIncidente contenido,
        IReadOnlyList<DestinatariosAvisoSeguridadTenant> destinatarios,
        CancellationToken cancellationToken)
    {
        var referencia = PlantillaAvisoIncidente.Referencia(incidente.Id, incidente.DetectadoEnUtc);
        var nombres = await plataforma.NombresTenantsAsync(incidente.TenantsAfectados, cancellationToken);
        var preparados = new List<EnvioPreparado>();
        var sinDestinatario = new List<Guid>();
        foreach (var tenant in destinatarios)
        {
            if (tenant.Destinatarios.Count == 0)
            {
                sinDestinatario.Add(tenant.TenantId);
                continue;
            }

            var organizacion = nombres.TryGetValue(tenant.TenantId, out var nombre) ? nombre : "vuestra organización";
            foreach (var destinatario in tenant.Destinatarios)
            {
                preparados.Add(new EnvioPreparado(tenant.TenantId, destinatario.Correo, new EnvioAvisoSolicitado(
                    incidente.Id, plantilla, tenant.TenantId, destinatario.Nombre, destinatario.Correo,
                    tenant.SinContactoValidado,
                    PlantillaAvisoIncidente.Asunto(plantilla, referencia, organizacion),
                    PlantillaAvisoIncidente.Cuerpo(plantilla, contenido, referencia, organizacion, destinatario.Nombre,
                        tenant.SinContactoValidado, confirmadoEnUtc))));
            }
        }

        return (preparados, sinDestinatario);
    }

    public static async Task<Result<ResumenEnvioAviso>> EnviarAsync(
        IIncidentesSeguridadPlataforma plataforma,
        IContactosSeguridadPlataforma contactos,
        IEmailService correo,
        OpcionesIncidentesSeguridad opciones,
        ILogger logger,
        IncidenteSeguridadDto incidente,
        DateTime confirmadoEnUtc,
        PlantillaAvisoIncidenteSeguridad plantilla,
        ContenidoAvisoIncidente contenido,
        IReadOnlyList<DestinatariosAvisoSeguridadTenant> destinatarios,
        CancellationToken cancellationToken)
    {
        var responderA = string.IsNullOrWhiteSpace(opciones.CorreoContactoSeguridadTalveg)
            ? null : opciones.CorreoContactoSeguridadTalveg.Trim();
        var (preparados, sinDestinatario) = await PrepararAsync(
            plataforma, incidente, confirmadoEnUtc, plantilla, contenido, destinatarios, cancellationToken);
        if (preparados.Any(p => DemasiadoLargo(p.Solicitado)))
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.CuerpoDemasiadoLargo);

        int enviados = 0, yaEnviados = 0, fallidos = 0, enCurso = 0;
        foreach (var (tenantId, correoDestinatario, solicitado) in preparados)
        {
            var reclamo = await plataforma.ReclamarEnvioAsync(solicitado, cancellationToken);
            if (reclamo.EsFallido)
            {
                if (!EsFalloDelDestinatario(reclamo.Error))
                    return Result.Fallo<ResumenEnvioAviso>(reclamo.Error);

                fallidos++;
                logger.LogWarning("La base rechazó un destinatario del Tenant {TenantId} para el aviso {Plantilla} del incidente {IncidenteId}: {Error}.",
                    tenantId, plantilla, incidente.Id, reclamo.Error.Codigo);
                continue;
            }

            var envio = reclamo.Valor;
            if (!envio.Reclamado)
            {
                if (envio.Estado == EstadoEnvioAvisoIncidenteSeguridad.Enviado) yaEnviados++;
                else enCurso++;
                continue;
            }

            // Sin el token de la petición: cerrar la página a mitad no puede dejar el envío a medias sin
            // anotar si salió.
            var resultado = await correo.EnviarAsync(
                correoDestinatario, envio.Asunto, envio.Cuerpo, TipoAvisoCorreo.Seguridad, responderA,
                CancellationToken.None);
            var anotado = await plataforma.ResolverEnvioAsync(
                envio.EnvioId, resultado.EsExitoso, resultado.EsFallido ? resultado.Error.Codigo : null,
                CancellationToken.None);
            if (anotado.EsFallido)
                logger.LogError("No se pudo anotar el envío {EnvioId} del aviso del incidente {IncidenteId}: {Error}.",
                    envio.EnvioId, incidente.Id, anotado.Error.Codigo);

            if (resultado.EsExitoso)
            {
                enviados++;
            }
            else
            {
                fallidos++;
                logger.LogWarning("El aviso {Plantilla} del incidente {IncidenteId} no salió hacia un destinatario del Tenant {TenantId}: {Error}.",
                    plantilla, incidente.Id, tenantId, resultado.Error.Codigo);
            }
        }

        return Result.Exito(new ResumenEnvioAviso(enviados, yaEnviados, fallidos, enCurso, sinDestinatario));
    }
}
