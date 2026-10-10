using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.GuardarOrdenCajasFicha;

/// <summary>
/// Guarda el orden en que el usuario actual quiere ver las cajas de la pestaña
/// «Ficha» de un tipo de ficha 360, en el Tenant actual (decisión del
/// 2026-10-09). Sustituye entero el orden que tuviera: hay una sola fila por
/// Tenant, usuario y tipo de ficha.
///
/// Es autoservicio: el usuario sale de <see cref="ICurrentUserService"/>, la
/// fila es suya y los handlers solo se la dan a él (la política RLS aísla por
/// Tenant; el usuario lo acotan ellos), así que cualquier rol que pueda ver la
/// ficha puede ordenar la suya. El comando no conoce qué cajas tiene cada ficha:
/// valida la forma de las claves y la lectura concilia el orden con las cajas
/// que la pantalla pinta (<see cref="OrdenCajasFicha.Conciliar"/>).
/// </summary>
public record GuardarOrdenCajasFichaCommand(string TipoFicha, IReadOnlyList<string> Claves) : ICommand, IComandoDeAutoservicio;

/// <summary>
/// Las fichas 360 cuyo orden de cajas se puede guardar: una por página de ficha.
/// El valor se persiste; cambiarlo deja huérfanos los órdenes ya guardados.
/// Nombran la página, no un tipo de Empresa: <see cref="Cliente"/> es la ficha
/// del Cliente empresarial (<c>/clientes/{id}</c>) y <see cref="Subcontrata"/>
/// la de una Empresa en su papel de subcontratista (<c>/subcontratas/{id}</c>).
/// </summary>
public static class TiposDeFicha360
{
    public const string Cliente = "Cliente";
    public const string Empresa = "Empresa";
    public const string Subcontrata = "Subcontrata";
    public const string Centro = "Centro";
    public const string Trabajador = "Trabajador";
    public const string Vehiculo = "Vehiculo";
    public const string Proyecto = "Proyecto";
    public const string Visita = "Visita";
    public const string TipoDocumento = "TipoDocumento";

    public static readonly string[] Admitidos =
        [Cliente, Empresa, Subcontrata, Centro, Trabajador, Vehiculo, Proyecto, Visita, TipoDocumento];
}

public class GuardarOrdenCajasFichaCommandValidator : AbstractValidator<GuardarOrdenCajasFichaCommand>
{
    public GuardarOrdenCajasFichaCommandValidator()
    {
        RuleFor(c => c.TipoFicha).Must(t => TiposDeFicha360.Admitidos.Contains(t))
            .WithMessage("Ese tipo de ficha no admite un orden de cajas.");
        RuleFor(c => c.Claves).Custom((claves, contexto) =>
        {
            if (OrdenCajasFicha.ErrorDeForma(claves) is { } error)
                contexto.AddFailure(nameof(GuardarOrdenCajasFichaCommand.Claves), error);
        });
    }
}

public class GuardarOrdenCajasFichaCommandHandler(
    ICurrentUserService currentUserService, ITenantActual tenantActual,
    IOrdenCajasFichaRepository repositorio, IUnitOfWork unitOfWork, TimeProvider reloj)
    : IRequestHandler<GuardarOrdenCajasFichaCommand, Result>
{
    public async Task<Result> Handle(GuardarOrdenCajasFichaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("OrdenCajasFicha.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Sin Tenant activo no se guarda: la fila se sella con él. Fallo legible
        // aquí, antes de que lo rechace el sellado.
        if (tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty)
            return Result.Fallo(Error.Crear(
                "OrdenCajasFicha.SinTenant",
                "No pudimos determinar en qué organización estás trabajando. Vuelve a iniciar sesión."));

        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        var orden = await repositorio.ObtenerAsync(usuarioId.Value, request.TipoFicha, cancellationToken);
        if (orden is null)
            repositorio.Agregar(new OrdenCajasFicha(usuarioId.Value, request.TipoFicha, request.Claves, ahoraUtc));
        else
            orden.Reordenar(request.Claves, ahoraUtc);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
