using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;

/// <summary>
/// Registra el Encargo de administración del Tenant activo a favor del Operador
/// CAE externo de una de sus operaciones (decisión D-8, 2026-10-08). El Tenant
/// propietario es siempre el activo, nunca un parámetro.
///
/// <para>
/// <b>Autoridad</b> (<see cref="AutoridadSobreElEncargo"/>): un Administrador
/// propio del Tenant propietario, o Soporte TALVEG dentro de una Sesión
/// Privilegiada con la capacidad de aprovisionamiento sobre ese Tenant. Por eso
/// es <see cref="IComandoDeAprovisionamiento"/>: bajo esa sesión el comando
/// escribe con <c>cae_app_aprovisionamiento</c>, que puede insertar el encargo y
/// leer la operación a la que se liga. Nunca el rol efectivo, y nunca nadie del
/// Operador CAE que lo recibe.
/// </para>
///
/// <para>
/// La cláusula del contrato es obligatoria. Solo puede haber un encargo sin
/// retirar por operación: para cambiar la cláusula o la vigencia se retira el
/// vigente y se registra otro.
/// </para>
/// </summary>
[AreaDelEncargo("Tenants")]
public record RegistrarEncargoAdministracionCommand(
    Guid AsignacionOperacionId, string ClausulaContrato, DateTime? VigenciaHasta)
    : ICommand<Guid>, IComandoDeAprovisionamiento;

public class RegistrarEncargoAdministracionCommandValidator : AbstractValidator<RegistrarEncargoAdministracionCommand>
{
    public RegistrarEncargoAdministracionCommandValidator()
    {
        RuleFor(c => c.AsignacionOperacionId).NotEmpty().WithMessage("Selecciona el Operador CAE externo que recibe el encargo.");
        RuleFor(c => c.ClausulaContrato)
            .Must(clausula => !string.IsNullOrWhiteSpace(clausula))
            .WithMessage(ErroresEncargoAdministracion.ClausulaObligatoria.Mensaje)
            .MaximumLength(EncargoAdministracion.LongitudMaximaClausula);
    }
}

public class RegistrarEncargoAdministracionCommandHandler(
    ITenantActual tenantActual,
    AutoridadSobreElEncargo autoridad,
    IEncargoAdministracionRepository encargos,
    TimeProvider reloj,
    IOutboundCerradoTenantTec outboundCerrado)
    : IRequestHandler<RegistrarEncargoAdministracionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        RegistrarEncargoAdministracionCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va antes que cualquier lectura: quien no puede no debe poder
        // distinguir, por el mensaje de error, qué operaciones existen.
        if (tenantActual.TenantId is not { } propietarioTenantId
            || await autoridad.ResolverAsync(propietarioTenantId, cancellationToken) is not { } quienRegistra)
            return Result.Fallo<Guid>(AutoridadSobreElEncargo.NoAutorizado);

        // Se repite lo del validador: el handler no depende de que el pipeline lo haya ejecutado.
        if (string.IsNullOrWhiteSpace(request.ClausulaContrato))
            return Result.Fallo<Guid>(ErroresEncargoAdministracion.ClausulaObligatoria);
        if (request.ClausulaContrato.Trim().Length > EncargoAdministracion.LongitudMaximaClausula)
            return Result.Fallo<Guid>(ErroresEncargoAdministracion.ClausulaDemasiadoLarga);

        var operacion = await encargos.ObtenerOperacionAsync(
            request.AsignacionOperacionId, propietarioTenantId, cancellationToken);
        // La operación raíz y las internas tienen por operador al propio Tenant propietario: no hay
        // Operador CAE externo a quien encargar. Va antes del rechazo de abajo porque en ellas ese
        // rechazo confundiría al Administrador propio con «alguien del Operador CAE».
        if (operacion is null || operacion.EsRaiz || operacion.EsOperacionInterna)
            return Result.Fallo<Guid>(ErroresEncargoAdministracion.OperacionNoValida);

        // Nunca nadie del Operador CAE que lo recibe, aunque la autoridad de arriba ya lo excluya.
        if (await autoridad.EsDelOperadorCaeAsync(operacion.OperadorTenantId))
            return Result.Fallo<Guid>(AutoridadSobreElEncargo.NoAutorizado);

        // D3 (EP-4: sin línea Outbound, el Tenant TEC participante no es Operador CAE de nadie). El behavior ya decidió por
        // el Tenant beneficiario; el encargo amplía lo que hace el Operador CAE de la operación, así que se decide también
        // por su Tenant de origen. Mismo error que el resto de D3. Con Sesión Privilegiada esto corre ya elevado: el marcador
        // se lee igual (cae_app_aprovisionamiento tiene SELECT sobre Tenants) y, si está marcado, la línea responde «sin
        // línea» porque ese Tenant nunca es el activo aquí: falla cerrado. shortcut: LimiteTenantTecDelOperadorCae.
        if (await outboundCerrado.EstaCerradoAsync(operacion.OperadorTenantId, cancellationToken))
            return Result.Fallo<Guid>(ErroresLimiteTenantTec.OutboundCerrado);

        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (!operacion.Ambito.EsUniversal || !operacion.EstaVigenteEn(ahora))
            return Result.Fallo<Guid>(ErroresEncargoAdministracion.OperacionNoValida);

        if (request.VigenciaHasta is { } hasta && hasta <= ahora)
            return Result.Fallo<Guid>(ErroresEncargoAdministracion.VigenciaNoValida);

        if (await encargos.ExisteSinRetirarAsync(operacion.Id, cancellationToken))
            return Result.Fallo<Guid>(ErroresEncargoAdministracion.YaRegistrado);

        var encargo = EncargoAdministracion.Registrar(
            operacion, request.ClausulaContrato, EncargoAdministracion.VersionTextoVigente,
            quienRegistra.Origen, quienRegistra.ActorRealUsuarioId, ahora, request.VigenciaHasta);
        encargos.Agregar(encargo);

        // Dos registros a la vez: el índice único deja pasar uno.
        return await encargos.GuardarDetectandoCarreraAsync(cancellationToken)
            ? Result.Exito(encargo.Id)
            : Result.Fallo<Guid>(ErroresEncargoAdministracion.YaRegistrado);
    }
}
