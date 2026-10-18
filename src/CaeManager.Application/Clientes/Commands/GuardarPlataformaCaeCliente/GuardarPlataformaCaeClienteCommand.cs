using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Commands.GuardarPlataformaCaeCliente;

/// <summary>
/// Alta o edición (upsert) de la Plataforma CAE de un Cliente empresarial: nombre, dirección
/// del portal y código de cliente. Modal «Editar plataforma CAE» de Cliente 360 (maqueta del
/// 17-10). No toca credenciales: el usuario y la contraseña de cada Empresa los guarda
/// <c>GuardarAccesoPlataformaCaeCommand</c>.
///
/// <para>
/// Roles: <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> deniega a Consulta,
/// al Usuario de Cliente y a toda Sesión Privilegiada (impersonación incluida). El handler
/// añade la cartera: el Cliente empresarial tiene que estar en el ámbito efectivo del usuario
/// y quien escribe tiene que operar desde el lado de gestión. Y la Empresa tiene que ser hoy
/// Cliente empresarial: posición cliente de al menos una Relación Empresarial vigente. Sin
/// eso no hay a quién acreditar en la plataforma.
/// </para>
/// </summary>
public record GuardarPlataformaCaeClienteCommand(Guid ClienteEmpresarialId, string Nombre, string? UrlPortal, string? CodigoCliente) : ICommand;

public class GuardarPlataformaCaeClienteCommandValidator : AbstractValidator<GuardarPlataformaCaeClienteCommand>
{
    public GuardarPlataformaCaeClienteCommandValidator()
    {
        RuleFor(c => c.ClienteEmpresarialId).NotEmpty();
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("Indica la plataforma.")
            .MaximumLength(PlataformaCaeClienteEmpresarial.LongitudMaximaNombre);
        RuleFor(c => c.UrlPortal).MaximumLength(PlataformaCaeClienteEmpresarial.LongitudMaximaUrlPortal)
            .Must(url => string.IsNullOrWhiteSpace(url) || PlataformaCaeClienteEmpresarial.EsUrlDePortalValida(url.Trim()))
            .WithMessage("La dirección del portal debe empezar por https:// o http://.");
        RuleFor(c => c.CodigoCliente).MaximumLength(PlataformaCaeClienteEmpresarial.LongitudMaximaCodigoCliente);
    }
}

public class GuardarPlataformaCaeClienteCommandHandler(
    IEmpresaRepository empresaRepositorio,
    IEmpresasQueryContext empresasContext,
    IPlataformaCaeClienteEmpresarialRepository plataformaRepositorio,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<GuardarPlataformaCaeClienteCommand, Result>
{
    private static readonly Error NoEncontrado = Error.Crear("Cliente.NoEncontrado", "No encontramos este Cliente.");

    public async Task<Result> Handle(GuardarPlataformaCaeClienteCommand request, CancellationToken cancellationToken)
    {
        // Fuera de la cartera se responde como si no existiera (Issue #18).
        var cliente = await empresaRepositorio.ObtenerPorIdAsync(request.ClienteEmpresarialId, cancellationToken);
        if (cliente is null
            || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return Result.Fallo(NoEncontrado);

        if (!await empresasContext.RelacionesEmpresariales.AnyAsync(
                r => r.ClienteId == cliente.Id && r.VigenciaHasta == null, cancellationToken))
            return Result.Fallo(Error.Crear(
                "PlataformaCae.SinRelacionVigente", "Esta Empresa no es hoy Cliente de ninguna Empresa propia."));

        var plataforma = await plataformaRepositorio.ObtenerPlataformaAsync(cliente.Id, cancellationToken);
        if (plataforma is null)
        {
            plataformaRepositorio.AgregarPlataforma(
                new PlataformaCaeClienteEmpresarial(cliente.Id, request.Nombre, request.UrlPortal, request.CodigoCliente));
        }
        else
        {
            plataforma.Actualizar(request.Nombre, request.UrlPortal, request.CodigoCliente);
        }

        return await plataformaRepositorio.GuardarCambiosAsync(cancellationToken)
            ? Result.Exito()
            : Result.Fallo(Error.Crear(
                "PlataformaCae.Conflicto", "Otra persona acaba de registrar la plataforma de este Cliente. Cierra y vuelve a abrirla para ver lo guardado."));
    }
}
