using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Cumplimiento.Commands.RegistrarInstruccionTratamientoIaTenantPropietario;

/// <summary>
/// Registra la instrucción documentada que habilita, para un Tenant
/// propietario, el tratamiento de datos personales mediante IA (Nivel 0,
/// DEC-33/REC-035) — nunca autogestionado por el propio tenant: lo registra
/// un Administrador de plataforma a partir de una relación contractual ya
/// cerrada fuera del sistema (DPA firmado, Anexo II aceptado), mismo
/// criterio de alcance que <c>RegistrarSuscripcionTenantCommand</c>
/// (ADR-009 § 2.5) y <c>GenerarClaveApiCommand</c> (P3-29).
///
/// Autorización por <see cref="IAutorizacionAdminPlataforma.PuedeSobreTenantAsync"/>
/// — la capacidad AdminPlataforma sobre ESTE tenant concreto, no
/// "el tenant de origen es la plataforma": esa comprobación más antigua
/// (<c>AbrirAccesoSoporteCommand</c>) no pasa por el modelo de concesiones
/// de ADR-011 § 4bis: aquí sí, porque este comando es nuevo y no tiene
/// deuda que arrastrar.
/// </summary>
public record RegistrarInstruccionTratamientoIaTenantPropietarioCommand(
    Guid TenantPropietarioId,
    string VersionDpaAceptada,
    string VersionAnexoSubencargadosAceptada) : ICommand<Guid>;

public class RegistrarInstruccionTratamientoIaTenantPropietarioCommandValidator
    : AbstractValidator<RegistrarInstruccionTratamientoIaTenantPropietarioCommand>
{
    public RegistrarInstruccionTratamientoIaTenantPropietarioCommandValidator()
    {
        RuleFor(c => c.TenantPropietarioId).NotEmpty();

        RuleFor(c => c.VersionDpaAceptada)
            .NotEmpty().WithMessage("Indica qué versión del DPA aceptó este tenant.")
            .MaximumLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion);

        RuleFor(c => c.VersionAnexoSubencargadosAceptada)
            .NotEmpty().WithMessage("Indica qué versión del Anexo II de subencargados aceptó este tenant.")
            .MaximumLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion);
    }
}

public class RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler(
    ITenantsQueryContext dbContext,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IInstruccionTratamientoIaTenantPropietarioRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarInstruccionTratamientoIaTenantPropietarioCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        RegistrarInstruccionTratamientoIaTenantPropietarioCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("InstruccionTratamientoIa.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, request.TenantPropietarioId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "InstruccionTratamientoIa.SinAutoridad",
                "No tienes capacidad de administración de plataforma sobre ese tenant."));

        // No se distingue "no existe" de "es el tenant de plataforma" en el
        // mensaje: TALVEG no se instruye a sí misma tratamiento de datos de
        // terceros, y ninguna de las dos respuestas debe revelar cuál de las
        // dos ocurrió.
        var tenantValido = await dbContext.Tenants
            .AnyAsync(t => t.Id == request.TenantPropietarioId && !t.EsPlataforma, cancellationToken);
        if (!tenantValido)
            return Result.Fallo<Guid>(Error.Crear("Tenant.NoEncontrado", "No encontramos ese tenant."));

        // La fila pertenece al Tenant propietario elegido, no al tenant de
        // origen del administrador que la registra — sin este ámbito
        // explícito, el interceptor la sellaría contra el tenant de
        // plataforma (ver AmbitoTenantExplicito, mismo patrón que
        // GenerarClaveApiCommand). Y no solo la escritura: InstruccionesTratamientoIaTenantPropietario
        // lleva RLS + filtro global de EF por TenantId (a diferencia de
        // DelegacionTenant/ClaveApi en los precedentes citados arriba), así
        // que la comprobación de "¿ya hay una vigente?" tiene que leer bajo
        // el MISMO ámbito que la escritura — leerla antes de abrir el
        // ámbito filtraría por el tenant de origen del administrador y
        // siempre vería cero filas, sea cual sea el estado real del tenant
        // objetivo.
        using (AmbitoTenantExplicito.Establecer(request.TenantPropietarioId))
        {
            // Invariante de ObtenerVigenteAsync (a lo sumo una fila vigente
            // por tenant): una versión de DPA nueva se registra revocando
            // antes la vigente, nunca apilando una segunda fila vigente en
            // silencio — mismo criterio que DEC-43 contra la reapertura de
            // una ventana ya abierta.
            if (await repositorio.ObtenerVigenteAsync(request.TenantPropietarioId, cancellationToken) is not null)
                return Result.Fallo<Guid>(Error.Crear(
                    "InstruccionTratamientoIa.YaVigente",
                    "Este tenant ya tiene una instrucción vigente. Revócala antes de registrar una nueva versión."));

            var instruccion = new Domain.Cumplimiento.InstruccionTratamientoIaTenantPropietario(
                request.VersionDpaAceptada,
                request.VersionAnexoSubencargadosAceptada,
                DateTime.UtcNow,
                OrigenInstruccionTratamientoIa.AltaManualPlataforma,
                usuarioId.Value);

            repositorio.Agregar(instruccion);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Exito(instruccion.Id);
        }
    }
}
