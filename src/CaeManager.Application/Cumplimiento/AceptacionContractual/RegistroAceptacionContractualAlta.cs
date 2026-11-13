using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <param name="TenantId">El Tenant propietario recién creado, que debe ser el del ámbito actual.</param>
/// <param name="UsuarioId">La primera cuenta del Tenant, que ya tiene el rol de Administrador.</param>
/// <param name="SolicitudAltaId">La Solicitud de alta de la que salen las aceptaciones.</param>
/// <param name="Aceptaciones">La evidencia previa de la Solicitud de alta, documento a documento.</param>
/// <param name="ImportePermanencia">El importe de permanencia que se mostró al aceptar, sin impuestos.</param>
public sealed record RegistrarAceptacionContractualAlta(
    Guid TenantId,
    Guid UsuarioId,
    Guid SolicitudAltaId,
    IReadOnlyList<AceptacionDeclarada> Aceptaciones,
    decimal? ImportePermanencia);

/// <summary>
/// Materializa en el Tenant propietario las aceptaciones de la Solicitud de alta (#2009, paso 5 de
/// <c>CompletarAltaAutonomaCommand</c>). No es un <see cref="ICommand"/>: no lo pide ningún navegador.
/// Lo llama el Command del alta dentro de su propia transacción, en el ámbito del Tenant que acaba de
/// crear (<see cref="AmbitoTenantExplicito"/>), y es ese Command el que guarda. Por eso aquí solo se
/// añade a la unidad de trabajo.
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
    ICatalogoDocumentosContractuales catalogo,
    IAceptacionContractualTenantRepository repositorio,
    TimeProvider reloj)
    : IRegistroAceptacionContractualAlta
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result> RegistrarAsync(RegistrarAceptacionContractualAlta datos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(datos);

        if (datos.Aceptaciones is not { Count: > 0 })
            return Result.Fallo(ErroresAceptacionContractual.SinAceptaciones);
        if (datos.Aceptaciones.Any(a => a is null))
            return Result.Fallo(ErroresAceptacionContractual.DatoNoValido);
        if (datos.Aceptaciones.GroupBy(a => a.Documento?.Trim()).Any(g => g.Count() > 1))
            return Result.Fallo(ErroresAceptacionContractual.DocumentoRepetido);

        // Regla 2 de la #2009, con la misma invariante que la Solicitud de alta (PasarAPendientePago):
        // la declaración de facultades se hace una sola vez, en uno de los documentos.
        if (!datos.Aceptaciones.Any(a => a.DeclaraFacultades))
            return Result.Fallo(ErroresAceptacionContractual.SinDeclaracionFacultades);

        // El Tenant del ámbito es el que sella la fila (TenantSelladoInterceptor) y el que filtra RLS:
        // si no es el que se nombra, la evidencia acabaría en otro Tenant.
        if (datos.TenantId == Guid.Empty || tenantActual.TenantId != datos.TenantId)
            return Result.Fallo(ErroresAceptacionContractual.FueraDelTenant);

        // Reglas 2 y 3 de la #2009: acepta una cuenta propia del Tenant propietario, activa y con rol de
        // Administrador. La cuenta de un Operador CAE externo pertenece a su organización, no al Tenant
        // propietario, así que no pasa: no hay todavía ninguna acreditación con la que pueda aceptar en
        // nombre de otro (D-R2: el alta por un Operador CAE externo la hace un Actor de Plataforma TALVEG).
        if (datos.UsuarioId == Guid.Empty
            || !await directorioUsuarios.EsCuentaActivaConRolAsync(datos.UsuarioId, datos.TenantId, RolAdministrador, cancellationToken))
            return Result.Fallo(ErroresAceptacionContractual.SoloAdministradorDelTenantPropietario);

        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        if (datos.Aceptaciones.Any(a => a.AceptadaEnUtc > ahoraUtc))
            return Result.Fallo(ErroresAceptacionContractual.AceptacionFutura);

        // Cada aceptación se compara con la versión que regía cuando se aceptó, no con la de hoy: el
        // pago puede llegar después de que se publique otra. Versión y hash tienen que coincidir.
        var publicados = catalogo.Publicados;
        foreach (var aceptacion in datos.Aceptaciones)
        {
            var vigente = ResolucionDocumentosContractuales.VigenteEn(publicados, aceptacion.Documento?.Trim() ?? string.Empty, aceptacion.AceptadaEnUtc);
            if (vigente is null
                || vigente.Version != aceptacion.Version?.Trim()
                || !string.Equals(vigente.HashSha256, aceptacion.HashSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Fallo(ErroresAceptacionContractual.VersionNoPublicada);
        }

        var instanteContratacion = datos.Aceptaciones.Max(a => a.AceptadaEnUtc);
        var aceptados = datos.Aceptaciones.Select(a => a.Documento.Trim()).ToHashSet(StringComparer.Ordinal);
        if (ResolucionDocumentosContractuales.VigentesEn(publicados, instanteContratacion)
            .Any(d => d.ObligatorioEnAlta && !aceptados.Contains(d.Documento)))
            return Result.Fallo(ErroresAceptacionContractual.FaltaDocumentoObligatorio);

        // El Command del alta se reintenta entero (estrategia de ejecución) y el webhook puede llegar
        // dos veces: si esta Solicitud de alta ya dejó sus aceptaciones, no se duplican. El índice único
        // de la tabla lo garantiza también en la base de datos.
        if (await repositorio.ExistenDeSolicitudAltaAsync(datos.SolicitudAltaId, cancellationToken))
            return Result.Exito();

        List<AceptacionContractualTenant> nuevas;
        try
        {
            nuevas = datos.Aceptaciones
                .Select(a => new AceptacionContractualTenant(datos.UsuarioId, datos.SolicitudAltaId, a, datos.ImportePermanencia, ahoraUtc))
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
