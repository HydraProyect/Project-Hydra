using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <param name="TenantId">El Tenant propietario recién creado, que debe ser el del ámbito actual.</param>
/// <param name="UsuarioId">La primera cuenta del Tenant, que ya tiene el rol de Administrador.</param>
/// <param name="Solicitud">
/// La Solicitud de alta cargada por el Command del alta. Las aceptaciones se leen de ella, no de una lista
/// aparte: así solo se registra lo que declaró quien verificó su correo en esa Solicitud de alta.
/// </param>
/// <param name="ImportePermanencia">El importe de permanencia que se mostró al aceptar, sin impuestos.</param>
public sealed record RegistrarAceptacionContractualAlta(
    Guid TenantId,
    Guid UsuarioId,
    SolicitudAlta Solicitud,
    decimal? ImportePermanencia);

/// <summary>
/// Materializa en el Tenant propietario las aceptaciones de la Solicitud de alta (#2009, paso 5 de
/// <c>CompletarAltaAutonomaCommand</c>). No es un <see cref="ICommand"/>: no lo pide ningún navegador.
/// Lo llama el Command del alta dentro de su propia transacción, en el ámbito del Tenant que acaba de
/// crear (<see cref="AmbitoTenantExplicito"/>), y es ese Command el que guarda. Por eso aquí solo se
/// añade a la unidad de trabajo.
///
/// <para>
/// Concurrencia: el Command del alta modifica la misma Solicitud de alta en esa transacción
/// (<see cref="SolicitudAlta.Completar"/>, con su versión de fila), así que de dos procesamientos
/// simultáneos solo uno confirma. El otro se deshace entero, estas filas incluidas, y al reintentarse
/// encuentra las aceptaciones ya registradas. El índice único (Tenant, Solicitud de alta, Documento)
/// cierra el mismo hueco en la base de datos.
/// </para>
/// </summary>
public interface IRegistroAceptacionContractualAlta
{
    Task<Result> RegistrarAsync(RegistrarAceptacionContractualAlta datos, CancellationToken cancellationToken = default);
}

public static class ErroresAceptacionContractual
{
    public static readonly Error SinAceptaciones = Error.Crear(
        "AceptacionContractual.SinAceptaciones", "No hay ninguna aceptación que registrar.");

    public static readonly Error DocumentoRepetido = Error.Crear(
        "AceptacionContractual.DocumentoRepetido", "Cada documento se acepta una sola vez.");

    public static readonly Error SinDeclaracionFacultades = Error.Crear(
        "AceptacionContractual.SinDeclaracionFacultades",
        "Quien acepta tiene que declarar que tiene facultades para contratar.");

    public static readonly Error FueraDelTenant = Error.Crear(
        "AceptacionContractual.FueraDelTenant", "La aceptación solo se registra en la organización que se está dando de alta.");

    public static readonly Error SoloAdministradorDelTenantPropietario = Error.Crear(
        "AceptacionContractual.SoloAdministradorDelTenantPropietario",
        "Solo el Administrador del Tenant propietario puede aceptar en su nombre.");

    public static readonly Error AceptanteNoAcreditado = Error.Crear(
        "AceptacionContractual.AceptanteNoAcreditado",
        "Las aceptaciones son de quien verificó su correo en la solicitud de alta, y esta cuenta no es la suya.");

    public static readonly Error VersionNoPublicada = Error.Crear(
        "AceptacionContractual.VersionNoPublicada",
        "La versión aceptada no coincide con la que estaba publicada en ese momento.");

    public static readonly Error FaltaDocumentoObligatorio = Error.Crear(
        "AceptacionContractual.FaltaDocumentoObligatorio", "Falta aceptar un documento obligatorio.");

    public static readonly Error AceptacionFutura = Error.Crear(
        "AceptacionContractual.AceptacionFutura", "Una aceptación no puede tener fecha futura.");

    public static readonly Error DatoNoValido = Error.Crear(
        "AceptacionContractual.DatoNoValido", "Los datos de la aceptación no son válidos.");
}

public sealed class RegistroAceptacionContractualAlta(
    ITenantActual tenantActual,
    IDirectorioUsuariosService directorioUsuarios,
    IGestionCuentasUsuario cuentas,
    ICatalogoDocumentosContractuales catalogo,
    IAceptacionContractualTenantRepository repositorio,
    TimeProvider reloj)
    : IRegistroAceptacionContractualAlta
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result> RegistrarAsync(RegistrarAceptacionContractualAlta datos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(datos);
        ArgumentNullException.ThrowIfNull(datos.Solicitud);

        var solicitud = datos.Solicitud;
        var aceptacionesSolicitud = solicitud.Aceptaciones
            .Select(a => new AceptacionDeclarada(a.Documento, a.Version, a.HashSha256, a.DeclaraFacultades, a.Ip, a.AceptadaEnUtc))
            .ToList();

        if (aceptacionesSolicitud.Count == 0)
            return Result.Fallo(ErroresAceptacionContractual.SinAceptaciones);
        if (aceptacionesSolicitud.GroupBy(a => a.Documento.Trim()).Any(g => g.Count() > 1))
            return Result.Fallo(ErroresAceptacionContractual.DocumentoRepetido);

        // Regla 2 de la #2009, con la misma invariante que la Solicitud de alta (PasarAPendientePago):
        // la declaración de facultades se hace una sola vez, en uno de los documentos.
        if (!aceptacionesSolicitud.Any(a => a.DeclaraFacultades))
            return Result.Fallo(ErroresAceptacionContractual.SinDeclaracionFacultades);

        // El Tenant del ámbito es el que sella la fila (TenantSelladoInterceptor) y el que filtra RLS:
        // si no es el que se nombra, la evidencia acabaría en otro Tenant. Una Solicitud de alta que ya
        // creó otro Tenant propietario no deja sus aceptaciones en este.
        if (datos.TenantId == Guid.Empty || tenantActual.TenantId != datos.TenantId
            || (solicitud.TenantIdCreado is { } creado && creado != datos.TenantId))
            return Result.Fallo(ErroresAceptacionContractual.FueraDelTenant);

        // El Command del alta se reintenta entero (estrategia de ejecución) y el webhook puede llegar
        // dos veces: si esta Solicitud de alta ya dejó sus aceptaciones, guardadas o pendientes de guardar
        // en esta unidad de trabajo, no se duplican. Va antes de las comprobaciones de estado que cambia
        // (cuenta, catálogo): lo ya registrado no deja de estarlo porque luego se desactive la cuenta o se
        // retire una versión. El índice único de la tabla lo garantiza también en la base de datos.
        if (await repositorio.ExistenDeSolicitudAltaAsync(solicitud.Id, cancellationToken))
            return Result.Exito();

        // Reglas 2 y 3 de la #2009: acepta una cuenta propia del Tenant propietario, activa y con rol de
        // Administrador. La cuenta de un Operador CAE externo pertenece a su organización, no al Tenant
        // propietario, así que no pasa: no hay todavía ninguna acreditación con la que pueda aceptar en
        // nombre de otro (D-R2: el alta por un Operador CAE externo la hace un Actor de Plataforma TALVEG).
        if (datos.UsuarioId == Guid.Empty
            || !await directorioUsuarios.EsCuentaActivaConRolAsync(datos.UsuarioId, datos.TenantId, RolAdministrador, cancellationToken))
            return Result.Fallo(ErroresAceptacionContractual.SoloAdministradorDelTenantPropietario);

        // Quien aceptó en la Solicitud de alta es quien verificó su correo; la cuenta a la que se le
        // atribuye la aceptación tiene que ser la de ese correo (el mismo criterio de normalización que
        // NormalizedEmail de Identity). Sin correo verificado no hay a quién atribuirla.
        if (solicitud.CorreoVerificadoEnUtc is null || string.IsNullOrEmpty(solicitud.CorreoNormalizado))
            return Result.Fallo(ErroresAceptacionContractual.AceptanteNoAcreditado);
        var cuenta = await cuentas.ObtenerAsync(datos.UsuarioId, cancellationToken);
        if (cuenta is null || string.IsNullOrWhiteSpace(cuenta.Email)
            || SolicitudAlta.NormalizarCorreo(cuenta.Email.Trim()) != solicitud.CorreoNormalizado)
            return Result.Fallo(ErroresAceptacionContractual.AceptanteNoAcreditado);

        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        if (aceptacionesSolicitud.Any(a => a.AceptadaEnUtc > ahoraUtc))
            return Result.Fallo(ErroresAceptacionContractual.AceptacionFutura);

        // Cada aceptación se compara con la versión que regía cuando se aceptó, no con la de hoy: el
        // pago puede llegar después de que se publique otra. Versión y hash tienen que coincidir.
        var publicados = catalogo.Publicados;
        foreach (var aceptacion in aceptacionesSolicitud)
        {
            var vigente = ResolucionDocumentosContractuales.VigenteEn(publicados, aceptacion.Documento.Trim(), aceptacion.AceptadaEnUtc);
            if (vigente is null
                || vigente.Version != aceptacion.Version.Trim()
                || !string.Equals(vigente.HashSha256, aceptacion.HashSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Fallo(ErroresAceptacionContractual.VersionNoPublicada);
        }

        var instanteContratacion = aceptacionesSolicitud.Max(a => a.AceptadaEnUtc);
        var aceptados = aceptacionesSolicitud.Select(a => a.Documento.Trim()).ToHashSet(StringComparer.Ordinal);
        if (ResolucionDocumentosContractuales.VigentesEn(publicados, instanteContratacion)
            .Any(d => d.ObligatorioEnAlta && !aceptados.Contains(d.Documento)))
            return Result.Fallo(ErroresAceptacionContractual.FaltaDocumentoObligatorio);

        List<AceptacionContractualTenant> nuevas;
        try
        {
            nuevas = aceptacionesSolicitud
                .Select(a => new AceptacionContractualTenant(datos.UsuarioId, solicitud.Id, a, datos.ImportePermanencia, ahoraUtc))
                .ToList();
        }
        catch (ArgumentException)
        {
            return Result.Fallo(ErroresAceptacionContractual.DatoNoValido);
        }

        foreach (var nueva in nuevas)
            repositorio.Agregar(nueva);
        return Result.Exito();
    }
}
