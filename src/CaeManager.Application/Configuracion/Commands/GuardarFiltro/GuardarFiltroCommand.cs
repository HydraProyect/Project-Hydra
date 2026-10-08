using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.GuardarFiltro;

/// <summary>
/// Guarda una combinación de filtros con nombre, para el usuario actual, en
/// una pantalla concreta y dentro del Tenant activo de la sesión (P3-31).
/// <see cref="ValoresJson"/> lo serializa y entiende la propia pantalla — este
/// Command no conoce la forma de los filtros de cada feature. La pantalla
/// envía solo su nombre (<see cref="PantallasConFiltrosGuardados.Admitidas"/>);
/// el Tenant lo añade el handler con
/// <see cref="PantallasConFiltrosGuardados.ClaveAlmacenada"/>, nunca la pantalla.
/// </summary>
public record GuardarFiltroCommand(string Pantalla, string Nombre, string ValoresJson) : ICommand<Guid>, IComandoDeAutoservicio;

public static class PantallasConFiltrosGuardados
{
    public const string Clientes = "Clientes";
    public const string Documentos = "Documentos";
    public const string Trabajadores = "Trabajadores";

    public static readonly string[] Admitidas = [Clientes, Documentos, Trabajadores];

    /// <summary>
    /// Longitud máxima de la columna <c>FiltrosGuardados.Pantalla</c>
    /// (<c>FiltroGuardadoConfiguration</c>). La clave compuesta de cualquier
    /// pantalla admitida tiene que caber: nombre + «@» + 32 hexadecimales.
    /// </summary>
    public const int LongitudMaximaDeLaClave = 50;

    /// <summary>
    /// Único sitio donde se compone la clave que se guarda en
    /// <see cref="FiltroGuardado.Pantalla"/>: la pantalla más el Tenant activo.
    /// La usan la lectura, la escritura, el borrado y la siembra de demo, para
    /// que ninguna pueda derivar de las otras.
    ///
    /// Un filtro guardado lleva dentro identificadores del Tenant propietario
    /// en el que se creó (Empresa, Cliente empresarial, Centro…). Un Gestor CAE
    /// de un Operador CAE externo trabaja sobre varios Tenant con el mismo
    /// usuario: sin el Tenant en la clave vería en cada uno los filtros de los
    /// demás, con identificadores ajenos dentro.
    ///
    /// Devuelve <c>null</c> sin Tenant resuelto: quien llama no lee ni escribe
    /// (fallo cerrado). Nunca se cae a la clave antigua sin Tenant — las filas
    /// que la llevan son anteriores a esta regla, no se sabe a qué Tenant
    /// pertenecían sus identificadores y por eso ya no se leen.
    /// </summary>
    public static string? ClaveAlmacenada(string pantalla, Guid? tenantId) =>
        tenantId is { } id && id != Guid.Empty ? $"{pantalla}@{id:N}" : null;

    /// <summary>
    /// ¿Es <paramref name="claveAlmacenada"/> la clave de una pantalla admitida
    /// en el Tenant indicado? Es la misma condición con la que se lee, puesta
    /// del revés: lo que no se puede ver tampoco se puede borrar.
    /// </summary>
    public static bool EsDelTenant(string claveAlmacenada, Guid? tenantId) =>
        Admitidas.Any(pantalla => ClaveAlmacenada(pantalla, tenantId) is { } clave
            && string.Equals(clave, claveAlmacenada, StringComparison.Ordinal));
}

public class GuardarFiltroCommandValidator : AbstractValidator<GuardarFiltroCommand>
{
    public GuardarFiltroCommandValidator()
    {
        RuleFor(c => c.Pantalla).Must(p => PantallasConFiltrosGuardados.Admitidas.Contains(p))
            .WithMessage("Esa pantalla no admite filtros guardados.");
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("Ponle un nombre a este filtro.").MaximumLength(100);
        RuleFor(c => c.ValoresJson).NotEmpty();
    }
}

public class GuardarFiltroCommandHandler(
    ICurrentUserService currentUserService, ITenantActual tenantActual,
    IFiltroGuardadoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarFiltroCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarFiltroCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("FiltroGuardado.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Sin Tenant activo no se guarda: un filtro sin Tenant en la clave
        // no sería de ningún Tenant, y sus identificadores sí lo son.
        var clave = PantallasConFiltrosGuardados.ClaveAlmacenada(request.Pantalla, tenantActual.TenantId);
        if (clave is null)
            return Result.Fallo<Guid>(Error.Crear(
                "FiltroGuardado.SinTenant",
                "No pudimos determinar en qué organización estás trabajando. Vuelve a iniciar sesión."));

        var filtro = new FiltroGuardado(usuarioId.Value, clave, request.Nombre, request.ValoresJson);
        repositorio.Agregar(filtro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(filtro.Id);
    }
}
