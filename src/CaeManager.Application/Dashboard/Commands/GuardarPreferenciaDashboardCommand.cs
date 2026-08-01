using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Dashboard.Commands;

public record GuardarPreferenciaDashboardCommand(IReadOnlyList<string> CodigosKpi) : IRequest<Result>;

public class GuardarPreferenciaDashboardCommandValidator : AbstractValidator<GuardarPreferenciaDashboardCommand>
{
    public GuardarPreferenciaDashboardCommandValidator()
    {
        RuleFor(c => c.CodigosKpi).NotEmpty().WithMessage("Selecciona al menos un KPI.");
        RuleForEach(c => c.CodigosKpi)
            .Must(codigo => CatalogoKpis.Todos.Any(k => k.Codigo == codigo))
            .WithMessage("Código de KPI desconocido.");
    }
}

public class GuardarPreferenciaDashboardCommandHandler(
    ICurrentUserService currentUserService, IPreferenciaDashboardUsuarioRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarPreferenciaDashboardCommand, Result>
{
    public async Task<Result> Handle(GuardarPreferenciaDashboardCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("PreferenciaDashboard.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var preferencia = await repositorio.ObtenerPorUsuarioIdAsync(usuarioId.Value, cancellationToken);

        if (preferencia is null)
            repositorio.Agregar(new PreferenciaDashboardUsuario(usuarioId.Value, request.CodigosKpi));
        else
            preferencia.ActualizarSeleccion(request.CodigosKpi);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
