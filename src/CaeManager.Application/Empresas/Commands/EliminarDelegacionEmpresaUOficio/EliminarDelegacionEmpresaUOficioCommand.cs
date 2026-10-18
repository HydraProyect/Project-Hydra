using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EliminarDelegacionEmpresaUOficio;

public enum CatalogoTrabajadoresEmpresa
{
    DelegacionEmpresa,
    OficioEmpresa,
}

/// <summary>
/// Elimina (borrado lógico) una Delegación o un Oficio de la Empresa desde Empresa 360. Se rechaza mientras algún
/// Trabajador lo tenga asignado: quitarlo antes es decisión de quien gestiona a esos Trabajadores, no un efecto
/// secundario de borrar el catálogo. Autorización y concurrencia como <c>GuardarNotaInternaEmpresaCommand</c>.
/// </summary>
public record EliminarDelegacionEmpresaUOficioCommand(
    Guid EmpresaId, CatalogoTrabajadoresEmpresa Catalogo, Guid Id, Guid Version) : ICommand;

public class EliminarDelegacionEmpresaUOficioCommandValidator : AbstractValidator<EliminarDelegacionEmpresaUOficioCommand>
{
    public EliminarDelegacionEmpresaUOficioCommandValidator()
    {
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.Catalogo).IsInEnum();
    }
}

public class EliminarDelegacionEmpresaUOficioCommandHandler(
    IEmpresaRepository empresas,
    ICatalogoTrabajadoresEmpresaRepository catalogo,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarDelegacionEmpresaUOficioCommand, Result>
{
    public async Task<Result> Handle(EliminarDelegacionEmpresaUOficioCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresas.ObtenerPorIdAsync(request.EmpresaId, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        EntidadBase? entidad;
        int enUso;
        string nombre;
        if (request.Catalogo == CatalogoTrabajadoresEmpresa.DelegacionEmpresa)
        {
            var delegacion = await catalogo.ObtenerDelegacionEmpresaAsync(request.Id, cancellationToken);
            entidad = delegacion?.EmpresaId == empresa.Id ? delegacion : null;
            enUso = entidad is null ? 0 : await catalogo.ContarTrabajadoresDeDelegacionEmpresaAsync(request.Id, cancellationToken);
            nombre = "esta delegación";
        }
        else
        {
            var oficio = await catalogo.ObtenerOficioAsync(request.Id, cancellationToken);
            entidad = oficio?.EmpresaId == empresa.Id ? oficio : null;
            enUso = entidad is null ? 0 : await catalogo.ContarTrabajadoresDeOficioAsync(request.Id, cancellationToken);
            nombre = "este oficio";
        }

        if (entidad is null)
            return Result.Fallo(Error.Crear("Catalogo.NoEncontrado", $"No encontramos {nombre}."));
        if (ConcurrenciaOptimista.Verificar(entidad, request.Version, nombre) is { } conflicto)
            return Result.Fallo(conflicto);
        if (enUso > 0)
            return Result.Fallo(Error.Crear("Catalogo.EnUso",
                $"No se puede eliminar {nombre}: {enUso} {(enUso == 1 ? "trabajador lo tiene" : "trabajadores lo tienen")} asignado."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Catalogo.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        entidad.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
