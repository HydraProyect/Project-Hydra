using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.GuardarFiltro;

/// <summary>
/// Guarda una combinación de filtros con nombre, para el usuario actual, en
/// una pantalla concreta y en el Tenant actual (P3-31; clave con Tenant desde
/// la decisión D4 del 2026-10-08). El nombre no se repite dentro de esa clave.
/// <see cref="ValoresJson"/> lo serializa y
/// entiende la propia pantalla — este Command no conoce la forma de los
/// filtros de cada feature.
///
/// <para>
/// El nombre reservado de la vista recordada
/// (<see cref="FiltroGuardado.NombreVistaRecordada"/>) no se puede usar aquí: esa
/// fila solo la escribe <c>GuardarVistaRecordadaCommand</c>. Lo rechaza el
/// validador, con o sin espacios alrededor y en cualquier capitalización, y por
/// debajo el constructor de <see cref="FiltroGuardado"/>.
/// </para>
/// </summary>
public record GuardarFiltroCommand(string Pantalla, string Nombre, string ValoresJson) : ICommand<Guid>, IComandoDeAutoservicio;

public static class PantallasConFiltrosGuardados
{
    public const string Clientes = "Clientes";
    public const string Documentos = "Documentos";
    public const string Trabajadores = "Trabajadores";

    public static readonly string[] Admitidas = [Clientes, Documentos, Trabajadores];
}

public class GuardarFiltroCommandValidator : AbstractValidator<GuardarFiltroCommand>
{
    public GuardarFiltroCommandValidator()
    {
        RuleFor(c => c.Pantalla).Must(p => PantallasConFiltrosGuardados.Admitidas.Contains(p))
            .WithMessage("Esa pantalla no admite filtros guardados.");
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("Ponle un nombre a este filtro.").MaximumLength(100);
        RuleFor(c => c.Nombre).Must(nombre => !FiltroGuardado.EsNombreReservado(nombre))
            .WithMessage("Ese nombre está reservado. Elige otro nombre.");
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

        // Sin Tenant activo no se guarda: el filtro lleva dentro identificadores de un Tenant
        // y la fila se sella con él. Fallo legible aquí, antes de que lo rechace el sellado.
        if (tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty)
            return Result.Fallo<Guid>(Error.Crear(
                "FiltroGuardado.SinTenant",
                "No pudimos determinar en qué organización estás trabajando. Vuelve a iniciar sesión."));

        // El índice único (Tenant, Usuario, Pantalla, Nombre) es quien lo garantiza;
        // esta comprobación solo convierte el caso corriente en un mensaje legible.
        if (await repositorio.ExisteConNombreAsync(usuarioId.Value, request.Pantalla, request.Nombre.Trim(), cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "FiltroGuardado.NombreDuplicado", "Ya tienes un filtro guardado con ese nombre en esta pantalla. Elige otro nombre."));

        var filtro = new FiltroGuardado(usuarioId.Value, request.Pantalla, request.Nombre, request.ValoresJson);
        repositorio.Agregar(filtro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(filtro.Id);
    }
}
