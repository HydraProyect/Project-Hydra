using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.RevelarContrasenaAccesoPlataformaCae;

/// <summary>
/// La contraseña con la que una Empresa entra en la Plataforma CAE de un Cliente empresarial.
/// Es la única vía que la descifra hacia la interfaz: «Mostrar» (se oculta sola a los 30 s) y
/// «Copiar» de la caja «Plataforma CAE» de Cliente 360 la piden con un clic explícito, nunca
/// como efecto de abrir la pantalla.
///
/// <para>
/// Decisión del propietario (2026-10-18): mostrar y copiar quedan en la auditoría. Cada
/// lectura efectiva se registra como <c>AccesoDatoSensible</c> del Tenant propietario —quién,
/// cuándo y qué fila, sin el secreto— ANTES de entregarla; si no se puede registrar, no se
/// entrega (<see cref="IRegistroAccesoDatoSensibleService"/>).
/// </para>
///
/// <para>
/// Quién puede leerla: solo los roles con escritura del Tenant propietario, con segundo
/// factor y fuera de toda Sesión Privilegiada (<see cref="IConsultaDeSecretosDeTenant"/>,
/// <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>: Consulta, Usuario
/// de Cliente, soporte e impersonación reciben <c>null</c>); y solo sobre un Cliente
/// empresarial y una Empresa de su cartera de gestión (este handler).
/// </para>
/// </summary>
public record RevelarContrasenaAccesoPlataformaCaeQuery(Guid ClienteEmpresarialId, Guid EmpresaId)
    : IRequest<ContrasenaAccesoPlataformaCaeDto?>, IConsultaDeSecretosDeTenant;

public record ContrasenaAccesoPlataformaCaeDto(string? Contrasena);

public class RevelarContrasenaAccesoPlataformaCaeQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos, IRegistroAccesoDatoSensibleService registroAcceso)
    : IRequestHandler<RevelarContrasenaAccesoPlataformaCaeQuery, ContrasenaAccesoPlataformaCaeDto?>
{
    public async Task<ContrasenaAccesoPlataformaCaeDto?> Handle(
        RevelarContrasenaAccesoPlataformaCaeQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN sobre las dos partes (REC-153): fuera de la cartera se responde
        // como si no existiera.
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken)
            || !await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        var fila = await dbContext.AccesosPlataformaCaeEmpresa
            .Where(a => a.ClienteEmpresarialId == request.ClienteEmpresarialId && a.EmpresaId == request.EmpresaId)
            .Select(a => new { a.Id, Dto = new ContrasenaAccesoPlataformaCaeDto(a.Contrasena) })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila?.Dto.Contrasena is null)
            return null;

        await registroAcceso.RegistrarAsync(nameof(AccesoPlataformaCaeEmpresa), fila.Id, cancellationToken);
        return fila.Dto;
    }
}
