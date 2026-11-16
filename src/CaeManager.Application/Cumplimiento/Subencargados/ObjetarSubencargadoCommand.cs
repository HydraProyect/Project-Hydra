using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Subencargados;

/// <summary>
/// El Administrador del Tenant propietario registra su objeción a un subencargado avisado o activo (#2010, D-7). La
/// objeción no frena el subencargado para los demás Tenants; su efecto lo decide lo que el subencargado admite: si
/// admite exclusión por Tenant, este Tenant deja de usarlo por su conmutador (hoy, la IA); si no, queda constancia de
/// que este Tenant puede resolver sin penalización. Una por Tenant y subencargado, y nunca se edita ni se borra.
/// Quién puede: <see cref="AutorizacionSubencargados.AdministradorDelTenantPropietarioAsync"/>.
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record ObjetarSubencargadoCommand(Guid SubencargadoId, string Motivo) : ICommand<EfectoObjecionSubencargado>;

public class ObjetarSubencargadoCommandValidator : AbstractValidator<ObjetarSubencargadoCommand>
{
    public ObjetarSubencargadoCommandValidator()
    {
        RuleFor(c => c.SubencargadoId).NotEmpty();
        RuleFor(c => c.Motivo).NotEmpty().WithMessage("Indica el motivo de la objeción.")
            .MaximumLength(ObjecionSubencargado.LongitudMaximaMotivo);
    }
}

public class ObjetarSubencargadoCommandHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAutorizacionAdminPlataforma autorizacion,
    ITenantsQueryContext tenants,
    ISubencargadoRepository repositorio,
    TimeProvider reloj)
    : IRequestHandler<ObjetarSubencargadoCommand, Result<EfectoObjecionSubencargado>>
{
    public async Task<Result<EfectoObjecionSubencargado>> Handle(ObjetarSubencargadoCommand request, CancellationToken cancellationToken)
    {
        var administrador = await AutorizacionSubencargados.AdministradorDelTenantPropietarioAsync(
            usuarioActual, tenantActual, sesionPrivilegiada, autorizacion, tenants, cancellationToken);
        if (administrador.EsFallido)
            return Result.Fallo<EfectoObjecionSubencargado>(administrador.Error);

        var subencargado = await repositorio.ObtenerAsync(request.SubencargadoId, cancellationToken);
        if (subencargado is null)
            return Result.Fallo<EfectoObjecionSubencargado>(ErroresSubencargado.NoEncontrado);
        if (!subencargado.AdmiteObjecion)
            return Result.Fallo<EfectoObjecionSubencargado>(ErroresSubencargado.NoAdmiteObjecion);
        if (await repositorio.ExisteObjecionAsync(subencargado.Id, cancellationToken))
            return Result.Fallo<EfectoObjecionSubencargado>(ErroresSubencargado.YaObjetado);

        // La fila se sella en el Tenant actual, que es el Tenant propietario del Administrador (no hay contexto cruzado).
        // Dos objeciones simultáneas del mismo Tenant pasan las dos la comprobación de arriba: el índice único deja
        // una, y la otra recibe la misma respuesta que si hubiera llegado después.
        var objecion = new ObjecionSubencargado(subencargado, administrador.Valor.UsuarioId, request.Motivo, reloj.GetUtcNow().UtcDateTime);
        repositorio.AgregarObjecion(objecion);
        if (!await repositorio.GuardarObjecionAsync(cancellationToken))
            return Result.Fallo<EfectoObjecionSubencargado>(ErroresSubencargado.YaObjetado);
        return Result.Exito(objecion.Efecto);
    }
}
