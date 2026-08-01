using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.CrearClienteDelegante;

/// <summary>
/// Alta de un Cliente Delegante nuevo con su Delegated Workspace ya
/// operativo — cierra ADR-004 § 12.2 con la decisión mínima que el propio
/// hallazgo P0-7 de docs/business/MATURITY_REVIEW.md dejaba abierta como
/// aceptable: "solo Administrador de plataforma en v1". Sin este Command no
/// había ningún camino de producto para aprovisionar el segmento consultora
/// — <c>CrearDelegacionTenantCommand</c>/<c>CrearAsignacionOperadorDelegadoCommand</c>
/// ya existían pero solo los despachaba <c>DelegacionDemoSeeder</c>.
///
/// Une en una sola operación lo que el seeder de demo hace en varios pasos:
/// aprovisiona el Tenant, lo une a la Consultora (el tenant de origen de
/// quien ejecuta esto) con una <see cref="DelegacionTenant"/> activa, y dado
/// que una delegación sin ningún Operador Delegado sería inservible, asigna
/// aquí mismo a quien la crea. Quién más puede iniciarla (¿el propio Cliente
/// Delegante?) sigue abierto — ver ADR-004 § 12.2.
///
/// A propósito NO copia el catálogo de <c>TipoDocumento</c> del tenant #1
/// (a diferencia de <c>DelegacionDemoSeeder</c>): ese catálogo de 40+ tipos
/// es una conveniencia de demo, no necesariamente lo que un Cliente
/// Delegante real necesita — la pantalla /tipos-documento ya permite darlos
/// de alta uno a uno tras crear el tenant, sin forzar una copia completa que
/// nadie pidió (YAGNI, ver PROJECT.md).
/// </summary>
public record CrearClienteDeleganteCommand(string NombreTenantCliente) : IRequest<Result<Guid>>;

public class CrearClienteDeleganteCommandValidator : AbstractValidator<CrearClienteDeleganteCommand>
{
    public CrearClienteDeleganteCommandValidator()
    {
        RuleFor(c => c.NombreTenantCliente)
            .NotEmpty().WithMessage("El nombre del Cliente Delegante es obligatorio.")
            .MaximumLength(Tenant.LongitudMaximaNombre);
    }
}

public class CrearClienteDeleganteCommandHandler(
    ITenantRepository tenantRepositorio,
    IDelegacionTenantRepository delegacionRepositorio,
    IAsignacionOperadorDelegadoRepository asignacionRepositorio,
    IParametroSistemaRepository parametroSistemaRepositorio,
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearClienteDeleganteCommand, Result<Guid>>
{
    /// <summary>
    /// Umbrales por defecto de alerta (ámbar/rojo, en días) para el
    /// ParametroSistema de un tenant nuevo — mismos valores que
    /// ParametroSistemaSeedData usa para el tenant #1. Se repiten aquí en
    /// vez de referenciarla porque esa clase vive en Infrastructure.Persistence.Seed,
    /// y Application no puede depender de Infrastructure — mismo motivo por
    /// el que CrearAsignacionOperadorDelegadoCommand repite los roles de
    /// Infrastructure.Identity.Roles en vez de importarlos.
    /// </summary>
    private const int UmbralAmbarDiasPorDefecto = 30;
    private const int UmbralRojoDiasPorDefecto = 15;

    /// <summary>
    /// Rol con el que queda operando quien crea el Cliente Delegante — mismo
    /// default que DelegacionDemoSeeder.RolOperadorDelegadoDemo. Gestor CAE
    /// y no Administrador/Consulta: puede trabajar de inmediato sobre el
    /// workspace sin heredar privilegios de administración de la
    /// organización del cliente (mismo criterio que CrearAsignacionOperadorDelegadoCommand).
    /// </summary>
    private const string RolInicialOperadorDelegado = "GestorCae";

    public async Task<Result<Guid>> Handle(CrearClienteDeleganteCommand request, CancellationToken cancellationToken)
    {
        // Solo desde el tenant marcado como plataforma, comprobado contra el
        // tenant de ORIGEN — nunca contra ITenantActual, que reflejaría un
        // Delegated Workspace ya activo (mismo criterio que
        // AbrirAccesoSoporteCommand).
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma)
            return Result.Fallo<Guid>(Error.Crear(
                "ClienteDelegante.SinPermiso", "Solo el administrador de la plataforma puede dar de alta un Cliente Delegante."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("ClienteDelegante.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (await tenantRepositorio.ExisteConNombreAsync(request.NombreTenantCliente, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("ClienteDelegante.NombreDuplicado", "Ya existe un tenant con este nombre."));

        var tenantCliente = new Tenant(request.NombreTenantCliente);

        // Ámbito explícito: la fila de ParametroSistema del tenant nuevo debe
        // sellarse contra SU PROPIO Id, no contra el tenant de origen de
        // quien ejecuta este Command — mismo mecanismo que
        // DelegacionDemoSeeder.AprovisionarTenantClienteAsync (docs/MULTITENANCY.md § 8.4).
        // Todo tenant necesita esta fila: ObtenerKpisDashboardQuery la lee
        // con SingleAsync() y falla si no existe.
        using (AmbitoTenantExplicito.Establecer(tenantCliente.Id))
        {
            tenantRepositorio.Agregar(tenantCliente);
            parametroSistemaRepositorio.Agregar(new ParametroSistema(UmbralAmbarDiasPorDefecto, UmbralRojoDiasPorDefecto));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Sin ámbito explícito aquí: DelegacionTenant/AsignacionOperadorDelegado
        // son catálogo global (Entity, no EntidadConTenant, ver sus propios
        // comentarios) — mismo tratamiento que AbrirAccesoSoporteCommand, que
        // tampoco lo usa para estas dos entidades.
        var delegacion = new DelegacionTenant(tenantOrigenId!.Value, tenantCliente.Id);
        delegacionRepositorio.Agregar(delegacion);

        // Sin esto, la delegación quedaría creada pero inservible: nadie
        // podría operarla hasta un segundo paso manual que además hoy no
        // tiene UI propia (Delegaciones.razor solo revoca/reactiva).
        asignacionRepositorio.Agregar(new AsignacionOperadorDelegado(delegacion.Id, usuarioId.Value, RolInicialOperadorDelegado));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(tenantCliente.Id);
    }
}
