using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <summary>Una versión vigente de un documento contractual obligatorio que el Tenant propietario aún no ha aceptado.</summary>
public sealed record DocumentoContractualPendienteDto(string Documento, string Titulo, string Version, string HashSha256);

/// <summary>Lo que el Administrador del Tenant propietario dice aceptar: el documento y la versión y el hash que se le mostraron.</summary>
public sealed record DocumentoContractualAceptado(string Documento, string Version, string HashSha256);

/// <summary>
/// Quién acepta fuera del alta (#2009, decisión D-6 del 2026-11-15): el Administrador del Tenant propietario, con
/// las mismas guardas de autoría que el registro del alta y la consulta del registro. Lo usan la consulta de
/// pendientes (que con cualquier otra cuenta devuelve una lista vacía y no bloquea) y el Command que acepta (que
/// la rechaza). Falla cerrado en cada paso:
/// <list type="number">
/// <item>sin Tenant actual o en el Tenant de plataforma, nadie: sus cuentas son las de los Actores de Plataforma
/// TALVEG, que no son parte del contrato con TALVEG;</item>
/// <item>bajo una Sesión Privilegiada (soporte, impersonación, aprovisionamiento…), nadie: un Actor de Plataforma
/// TALVEG no acepta en nombre de un Tenant propietario;</item>
/// <item>si el Actor real no es la cuenta de la sesión, o hay un Usuario simulado, nadie: acepta la persona que
/// está detrás del teclado y solo en su propio nombre;</item>
/// <item>rol efectivo de Administrador con el Tenant propietario como Tenant de origen
/// (<see cref="AutoridadSobreAceptacionContractual"/>): el Gestor CAE o el Administrador de un Operador CAE externo
/// que opera el Tenant no pasan;</item>
/// <item>y, leído en la base como en el alta, cuenta propia del Tenant, activa y con el rol de Administrador.</item>
/// </list>
/// </summary>
internal static class AceptanteFueraDelAlta
{
    public static async Task<Guid?> ResolverAsync(
        ICurrentUserService usuarioActual,
        ITenantActual tenantActual,
        ISesionPrivilegiadaActual sesionPrivilegiada,
        IActorAuditoria actorAuditoria,
        IDirectorioUsuariosService directorioUsuarios,
        ITenantsQueryContext tenants,
        CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId)
            return null;

        // La memo basta: aquí la sesión solo restringe. El Command, además, ya pasó por
        // AutorizacionEscrituraBehavior, que la revalidó y deja esa lectura fresca como memo.
        if (await sesionPrivilegiada.ObtenerAsync(cancellationToken) is not null)
            return null;

        if (await usuarioActual.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return null;

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.UsuarioSimuladoId is not null || actor.ActorRealUsuarioId != usuarioId)
            return null;

        if (!await AutoridadSobreAceptacionContractual.EsAdministradorDelTenantPropietarioAsync(usuarioActual, tenantActual))
            return null;

        if (!await AutoridadSobreAceptacionContractual.EsCuentaPropiaActivaDeAdministradorAsync(
                directorioUsuarios, usuarioId, tenantId, cancellationToken))
            return null;

        if (!await tenants.Tenants.AnyAsync(t => t.Id == tenantId && !t.EsPlataforma, cancellationToken))
            return null;

        return usuarioId;
    }

    /// <summary>La versión que rige ahora de cada documento obligatorio del catálogo.</summary>
    public static IReadOnlyList<DocumentoContractualPublicado> VigentesObligatorios(ICatalogoDocumentosContractuales catalogo, DateTime ahoraUtc) =>
        ResolucionDocumentosContractuales.VigentesEn(catalogo.Publicados, ahoraUtc).Where(d => d.ObligatorioEnAlta).ToList();

    /// <summary>
    /// Las vigentes que no tienen todavía una aceptación en el Tenant con el mismo documento, versión y hash,
    /// la haya hecho quien la haya hecho: lo aceptado obliga a la organización, no a la persona.
    /// </summary>
    public static IReadOnlyList<DocumentoContractualPublicado> Pendientes(
        IReadOnlyList<DocumentoContractualPublicado> vigentes, IReadOnlyList<AceptacionContractualTenant> aceptadas) =>
        vigentes
            .Where(v => !aceptadas.Any(a => a.Documento == v.Documento && a.Version == v.Version
                && string.Equals(a.HashSha256, v.HashSha256, StringComparison.OrdinalIgnoreCase)))
            .ToList();
}

/// <summary>
/// Lo que el Administrador del Tenant propietario tiene pendiente de aceptar (#2009, D-6): las versiones vigentes
/// ahora y obligatorias del catálogo sin aceptación en el Tenant. Es lo que abre <c>AceptacionContractualGate</c>.
///
/// <para>
/// Con cualquier otra cuenta (otro rol, Gestor CAE o Administrador de un Operador CAE externo, Actor de Plataforma
/// TALVEG, impersonación), la lista es vacía: no bloquea, porque no es quien puede aceptar
/// (<see cref="AceptanteFueraDelAlta"/>). Con el catálogo vacío también es vacía, y sin tocar la base: hoy
/// <c>CatalogoDocumentosContractuales</c> no publica nada, así que no se bloquea a nadie.
/// </para>
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record ObtenerAceptacionesContractualesPendientesQuery : IRequest<IReadOnlyList<DocumentoContractualPendienteDto>>;

public class ObtenerAceptacionesContractualesPendientesQueryHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IActorAuditoria actorAuditoria,
    IDirectorioUsuariosService directorioUsuarios,
    ITenantsQueryContext tenants,
    ICatalogoDocumentosContractuales catalogo,
    IAceptacionContractualTenantRepository repositorio,
    TimeProvider reloj)
    : IRequestHandler<ObtenerAceptacionesContractualesPendientesQuery, IReadOnlyList<DocumentoContractualPendienteDto>>
{
    public async Task<IReadOnlyList<DocumentoContractualPendienteDto>> Handle(
        ObtenerAceptacionesContractualesPendientesQuery request, CancellationToken cancellationToken)
    {
        var vigentes = AceptanteFueraDelAlta.VigentesObligatorios(catalogo, reloj.GetUtcNow().UtcDateTime);
        if (vigentes.Count == 0)
            return [];

        if (await AceptanteFueraDelAlta.ResolverAsync(
                usuarioActual, tenantActual, sesionPrivilegiada, actorAuditoria, directorioUsuarios, tenants, cancellationToken) is null)
            return [];

        var aceptadas = await repositorio.ListarAsync(cancellationToken);
        return AceptanteFueraDelAlta.Pendientes(vigentes, aceptadas)
            .Select(d => new DocumentoContractualPendienteDto(d.Documento, d.Titulo, d.Version, d.HashSha256))
            .ToList();
    }
}

/// <summary>
/// El Administrador del Tenant propietario acepta fuera del alta las versiones vigentes de los documentos
/// contractuales obligatorios (#2009, D-6): en su primer acceso cuando el alta la hizo un Operador CAE externo, o
/// cuando se publica una versión nueva. Registra una <see cref="AceptacionContractualTenant"/> por documento, sin
/// Solicitud de alta (<c>SolicitudAltaId = null</c>), y después del guardado envía el acuse con el texto aceptado.
///
/// <para>
/// Reglas: solo quien resuelve <see cref="AceptanteFueraDelAlta"/>; con la declaración de facultades; cada
/// documento una vez, y solo versiones vigentes ahora y obligatorias, con la versión y el hash que rigen; y tiene que
/// cubrir todas las pendientes. <see cref="Ip"/> es la del contexto HTTP en el que se pintó el aviso, la que lee
/// la Web como en el alta. Idempotente por (Tenant, cuenta, documento, versión): lo que esta cuenta ya aceptó no se
/// vuelve a registrar.
/// </para>
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record AceptarDocumentosContractualesCommand(
    IReadOnlyList<DocumentoContractualAceptado> Documentos, bool DeclaraFacultades, string? Ip) : ICommand;

public class AceptarDocumentosContractualesCommandHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IActorAuditoria actorAuditoria,
    IDirectorioUsuariosService directorioUsuarios,
    ITenantsQueryContext tenants,
    ICatalogoDocumentosContractuales catalogo,
    IAceptacionContractualTenantRepository repositorio,
    IUnitOfWork unitOfWork,
    IEnvioAcuseAceptacionContractual envioAcuse,
    TimeProvider reloj,
    ILogger<AceptarDocumentosContractualesCommandHandler> logger)
    : IRequestHandler<AceptarDocumentosContractualesCommand, Result>
{
    public static readonly Error SinIpDeOrigen = Error.Crear(
        "AceptacionContractual.SinIpDeOrigen", "No pudimos registrar tu aceptación. Recarga la página e inténtalo de nuevo.");

    public async Task<Result> Handle(AceptarDocumentosContractualesCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await AceptanteFueraDelAlta.ResolverAsync(
                usuarioActual, tenantActual, sesionPrivilegiada, actorAuditoria, directorioUsuarios, tenants, cancellationToken)
            is not { } usuarioId)
            return Result.Fallo(ErroresAceptacionContractual.SoloAdministradorDelTenantPropietario);

        if (!request.DeclaraFacultades)
            return Result.Fallo(ErroresAceptacionContractual.SinDeclaracionFacultades);

        var documentos = request.Documentos ?? [];
        if (documentos.Count == 0)
            return Result.Fallo(ErroresAceptacionContractual.SinAceptaciones);
        if (documentos.Any(d => d is null || string.IsNullOrWhiteSpace(d.Documento)))
            return Result.Fallo(ErroresAceptacionContractual.DatoNoValido);
        if (documentos.GroupBy(d => d.Documento.Trim(), StringComparer.Ordinal).Any(g => g.Count() > 1))
            return Result.Fallo(ErroresAceptacionContractual.DocumentoRepetido);

        if (string.IsNullOrWhiteSpace(request.Ip))
            return Result.Fallo(SinIpDeOrigen);

        // Cada documento, con la versión y el hash que rigen ahora: lo que se mostró tiene que ser lo publicado.
        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        var vigentes = AceptanteFueraDelAlta.VigentesObligatorios(catalogo, ahoraUtc);
        var aceptadosVigentes = new List<DocumentoContractualPublicado>();
        foreach (var documento in documentos)
        {
            var vigente = vigentes.FirstOrDefault(v => v.Documento == documento.Documento.Trim());
            if (vigente is null
                || vigente.Version != documento.Version?.Trim()
                || !string.Equals(vigente.HashSha256, documento.HashSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Fallo(ErroresAceptacionContractual.VersionNoPublicada);
            aceptadosVigentes.Add(vigente);
        }

        var aceptadas = await repositorio.ListarAsync(cancellationToken);
        var claves = aceptadosVigentes.Select(v => v.Documento).ToHashSet(StringComparer.Ordinal);
        if (AceptanteFueraDelAlta.Pendientes(vigentes, aceptadas).Any(p => !claves.Contains(p.Documento)))
            return Result.Fallo(ErroresAceptacionContractual.FaltaDocumentoObligatorio);

        // Idempotencia por (Tenant, cuenta, documento, versión): el repositorio ya lista solo el Tenant actual.
        // shortcut: la base no la respalda fuera del alta (su índice único exige SolicitudAltaId), así que dos
        // aceptaciones simultáneas de la misma cuenta pueden dejar dos filas iguales; mejorarlo con un índice único
        // parcial (TenantId, UsuarioId, Documento, Version) WHERE SolicitudAltaId IS NULL en su propia migración.
        var nuevas = aceptadosVigentes
            .Where(v => !aceptadas.Any(a => a.UsuarioId == usuarioId && a.Documento == v.Documento && a.Version == v.Version))
            .ToList();
        if (nuevas.Count == 0)
            return Result.Exito();

        List<AceptacionContractualTenant> filas;
        try
        {
            // La declaración de facultades se marca en cada fila: cada una se sostiene sola como evidencia, y una
            // versión nueva de un solo documento se acepta también con su declaración.
            filas = nuevas
                .Select(v => new AceptacionContractualTenant(
                    usuarioId, solicitudAltaId: null,
                    new AceptacionDeclarada(v.Documento, v.Version, v.HashSha256, DeclaraFacultades: true, request.Ip, ahoraUtc),
                    ahoraUtc))
                .ToList();
        }
        catch (ArgumentException)
        {
            return Result.Fallo(ErroresAceptacionContractual.DatoNoValido);
        }

        foreach (var fila in filas)
            repositorio.Agregar(fila);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // El acuse sale después del guardado y con su propia bandeja de salida (Preparado y luego Enviado o
        // NoConfirmado), nunca dentro de la transacción de la aceptación. Su fallo no deshace lo aceptado, que ya
        // está registrado: el Administrador puede pedir el reenvío (ReenviarAcuseAceptacionContractualCommand).
        try
        {
            var acuse = await envioAcuse.EnviarAsync(usuarioId, CancellationToken.None);
            if (acuse.EsFallido)
                logger.LogWarning(
                    "Aceptación contractual registrada sin acuse confirmado en el Tenant {TenantId}: {Codigo}.",
                    tenantActual.TenantId, acuse.Error.Codigo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Aceptación contractual registrada; el acuse no se pudo preparar en el Tenant {TenantId}.", tenantActual.TenantId);
        }

        return Result.Exito();
    }
}

/// <summary>El título y el texto exacto de una versión publicada de un documento contractual, para leerlo antes de aceptarlo.</summary>
public sealed record TextoDocumentoContractualDto(string Documento, string Titulo, string Version, DateTime VigenteDesdeUtc, string Texto);

/// <summary>
/// El texto de una versión publicada del catálogo (#2009), el que enlaza <c>AceptacionContractualGate</c>. Es el
/// texto del contrato con TALVEG, igual para todos los Tenants: no lee ningún dato del Tenant. <c>null</c> si esa
/// versión no está publicada.
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record ObtenerTextoDocumentoContractualQuery(string Documento, string Version) : IRequest<TextoDocumentoContractualDto?>;

public class ObtenerTextoDocumentoContractualQueryHandler(ICatalogoDocumentosContractuales catalogo)
    : IRequestHandler<ObtenerTextoDocumentoContractualQuery, TextoDocumentoContractualDto?>
{
    public Task<TextoDocumentoContractualDto?> Handle(ObtenerTextoDocumentoContractualQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(catalogo.Publicados
            .Where(d => d.Documento == request.Documento && d.Version == request.Version)
            .Select(d => new TextoDocumentoContractualDto(d.Documento, d.Titulo, d.Version, d.VigenteDesdeUtc, d.Texto))
            .FirstOrDefault());
}
