using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Commands.ProponerContactoSeguridad;

/// <summary>
/// El Administrador del Tenant beneficiario propone el contacto de seguridad de su organización
/// (decisión del propietario del 2026-10-26). El Tenant es siempre el actual, nunca un parámetro, y quien
/// propone tiene que ser Administrador de ese Tenant leído en la base
/// (<see cref="IAdministradorDelTenantPropietario"/>): con otro Tenant seleccionado por la vía de Operación,
/// un Gestor CAE o el Administrador del Operador CAE externo no pasan, y Soporte TALVEG en Sesión
/// Privilegiada tampoco (no pertenece al Tenant). La RLS repite la comprobación
/// (<c>app_es_administrador_del_tenant</c> en las políticas de alta y de sustitución).
///
/// <para>
/// La propuesta no cambia el contacto vigente: el validado sigue recibiendo avisos hasta que un Actor de
/// Plataforma TALVEG valide el nuevo. Si ya había otra propuesta pendiente, queda Sustituida en el mismo
/// guardado: solo hay una propuesta pendiente por Tenant. Si dos propuestas del mismo Tenant se cruzan, el
/// índice único parcial deja pasar una y la otra recibe <see cref="ErroresContactoSeguridad.PropuestaConcurrente"/>
/// en vez de un error sin controlar.
/// </para>
/// </summary>
public record ProponerContactoSeguridadCommand(string Nombre, string Correo, string? Telefono) : ICommand;

public class ProponerContactoSeguridadCommandValidator : AbstractValidator<ProponerContactoSeguridadCommand>
{
    public ProponerContactoSeguridadCommandValidator()
    {
        RuleFor(c => c.Nombre)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Indica el nombre de la persona de contacto.")
            .MaximumLength(ContactoSeguridadTenant.LongitudMaximaNombre)
            .WithMessage($"El nombre no puede superar {ContactoSeguridadTenant.LongitudMaximaNombre} caracteres.")
            .Must(SinCaracteresDeControl).WithMessage("El nombre no admite saltos de línea ni caracteres de control.");

        RuleFor(c => c.Correo)
            .Must(c => !string.IsNullOrWhiteSpace(c)).WithMessage("Indica el correo de la persona de contacto.")
            .MaximumLength(ContactoSeguridadTenant.LongitudMaximaCorreo)
            .WithMessage($"El correo no puede superar {ContactoSeguridadTenant.LongitudMaximaCorreo} caracteres.")
            .EmailAddress().WithMessage("El correo no tiene un formato válido.")
            // EmailAddress solo exige una arroba: un salto de línea en el correo acabaría en una cabecera del
            // aviso que lo use (incremento 3b).
            .Must(SinCaracteresDeControl).WithMessage("El correo no tiene un formato válido.");

        RuleFor(c => c.Telefono)
            .MaximumLength(ContactoSeguridadTenant.LongitudMaximaTelefono)
            .WithMessage($"El teléfono no puede superar {ContactoSeguridadTenant.LongitudMaximaTelefono} caracteres.")
            .Matches(@"^\+?[0-9 ().-]{6,}\z").WithMessage("El teléfono solo admite cifras, espacios, paréntesis, guiones y un + inicial.")
            .When(c => !string.IsNullOrWhiteSpace(c.Telefono));
    }

    private static bool SinCaracteresDeControl(string? valor) => valor is null || !valor.Any(char.IsControl);
}

public class ProponerContactoSeguridadCommandHandler(
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    IContactoSeguridadTenantRepository repositorio)
    : IRequestHandler<ProponerContactoSeguridadCommand, Result>
{
    public async Task<Result> Handle(ProponerContactoSeguridadCommand request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId
            || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || !await administradorDelTenant.EsAdministradorEnBaseAsync(usuarioId, tenantId, cancellationToken))
            return Result.Fallo(ErroresContactoSeguridad.SoloAdministradorDelTenant);

        var ahora = DateTime.UtcNow;
        foreach (var pendiente in (await repositorio.ObtenerPendienteYVigenteAsync(cancellationToken))
                     .Where(c => c.Estado == EstadoContactoSeguridad.Propuesto))
            pendiente.SustituirPropuestaPendiente(ahora);

        repositorio.Agregar(ContactoSeguridadTenant.Proponer(
            request.Nombre, request.Correo, request.Telefono, usuarioId, ahora));
        return await repositorio.GuardarPropuestaAsync(cancellationToken)
            ? Result.Exito()
            : Result.Fallo(ErroresContactoSeguridad.PropuestaConcurrente);
    }
}
