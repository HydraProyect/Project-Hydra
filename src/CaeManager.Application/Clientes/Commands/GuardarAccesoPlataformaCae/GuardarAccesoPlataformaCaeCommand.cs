using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Commands.GuardarAccesoPlataformaCae;

/// <summary>
/// Alta o edición (upsert) del acceso de una Empresa a la Plataforma CAE de un Cliente
/// empresarial: usuario y, si se escribe, contraseña nueva. Modal «Acceso de …» de
/// Cliente 360 (maqueta del 17-10).
///
/// <para>
/// <see cref="Contrasena"/> null, vacía o solo con espacios conserva la guardada (mismo criterio
/// que DEC-62): el formulario nunca la precarga, así que un campo vacío no puede significar
/// «bórrala». <see cref="Usuario"/> null también conserva el guardado; para quitarlo se envía
/// una cadena vacía (el modal envía siempre lo que hay en el campo).
/// La contraseña anterior no se conserva; el cambio queda en la auditoría con los valores
/// enmascarados (<c>AuditoriaInterceptor</c>), y la fila guarda cuándo y quién la cambió.
/// </para>
///
/// <para>
/// Autorización, en capas: <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>
/// deniega a Consulta, al Usuario de Cliente y a toda Sesión Privilegiada (impersonación
/// incluida); <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/> exige el
/// segundo factor (<see cref="IEscrituraDeDatosDeCredencial"/>); y el handler exige que el
/// Cliente empresarial y la Empresa estén en la cartera de gestión del usuario, que la
/// Empresa preste servicio hoy a ese Cliente empresarial (Relación Empresarial vigente, la
/// misma lista que pinta la caja) y que la plataforma del Cliente esté registrada.
/// </para>
/// </summary>
public record GuardarAccesoPlataformaCaeCommand(Guid ClienteEmpresarialId, Guid EmpresaId, string? Usuario, string? Contrasena)
    : ICommand, IEscrituraDeDatosDeCredencial;

public class GuardarAccesoPlataformaCaeCommandValidator : AbstractValidator<GuardarAccesoPlataformaCaeCommand>
{
    public GuardarAccesoPlataformaCaeCommandValidator()
    {
        RuleFor(c => c.ClienteEmpresarialId).NotEmpty();
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.Usuario).MaximumLength(AccesoPlataformaCaeEmpresa.LongitudMaximaUsuario);
        RuleFor(c => c.Contrasena).MaximumLength(AccesoPlataformaCaeEmpresa.LongitudMaximaContrasena);
    }
}

public class GuardarAccesoPlataformaCaeCommandHandler(
    IEmpresasQueryContext empresasContext,
    IPlataformaCaeClienteEmpresarialRepository plataformaRepositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService usuarioActual)
    : IRequestHandler<GuardarAccesoPlataformaCaeCommand, Result>
{
    private static readonly Error NoEncontrado = Error.Crear("AccesoPlataformaCae.NoEncontrado", "No encontramos esta Empresa en este Cliente.");

    private static readonly Error Conflicto = Error.Crear(
        "AccesoPlataformaCae.Conflicto", "Otra persona acaba de dar de alta este acceso. Cierra y vuelve a abrirlo para ver lo guardado.");

    public async Task<Result> Handle(GuardarAccesoPlataformaCaeCommand request, CancellationToken cancellationToken)
    {
        // Cartera de gestión sobre las dos partes. Fuera de ella se responde como si no
        // existiera: confirmar que la fila existe ya diría algo de datos que no corresponden.
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken)
            || !await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return Result.Fallo(NoEncontrado);

        // La misma lista que pinta la caja: Empresa propia que presta servicio hoy al Cliente
        // empresarial. Los filtros de Tenant del DbContext acotan las dos tablas.
        var prestaServicio = await (
            from r in empresasContext.RelacionesEmpresariales
            where r.ClienteId == request.ClienteEmpresarialId && r.ProveedoraId == request.EmpresaId && r.VigenciaHasta == null
            join empresa in empresasContext.Empresas.Where(e => e.EsPropia) on r.ProveedoraId equals empresa.Id
            select r.Id).AnyAsync(cancellationToken);
        if (!prestaServicio)
            return Result.Fallo(NoEncontrado);

        if (await plataformaRepositorio.ObtenerPlataformaAsync(request.ClienteEmpresarialId, cancellationToken) is null)
            return Result.Fallo(Error.Crear("AccesoPlataformaCae.SinPlataforma", "Registra antes la plataforma CAE de este Cliente."));

        var acceso = await plataformaRepositorio.ObtenerAccesoAsync(request.ClienteEmpresarialId, request.EmpresaId, cancellationToken);
        if (acceso is null)
        {
            acceso = new AccesoPlataformaCaeEmpresa(request.ClienteEmpresarialId, request.EmpresaId);
            plataformaRepositorio.AgregarAcceso(acceso);
        }

        if (request.Usuario is not null)
            acceso.CambiarUsuario(request.Usuario);
        if (!string.IsNullOrWhiteSpace(request.Contrasena))
            acceso.CambiarContrasena(request.Contrasena, await usuarioActual.ObtenerUsuarioActualIdAsync(), DateTime.UtcNow);

        return await plataformaRepositorio.GuardarCambiosAsync(cancellationToken) ? Result.Exito() : Result.Fallo(Conflicto);
    }
}
