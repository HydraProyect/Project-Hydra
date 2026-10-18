using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerAccesosPlataformaCaeCliente;

/// <summary>
/// «Accesos de cada Empresa» en la caja «Plataforma CAE» de Cliente 360: una fila por Empresa
/// propia que presta servicio hoy al Cliente empresarial (Relación Empresarial vigente) y está
/// en la cartera de gestión del usuario, con su usuario de la plataforma y cuándo y quién
/// cambió la contraseña por última vez. Nunca la contraseña: esa solo sale por
/// <c>RevelarContrasenaAccesoPlataformaCaeQuery</c>, con un clic explícito.
///
/// <para>
/// Descifra el <c>Usuario</c> (cifrado en reposo), así que está marcada con
/// <see cref="IConsultaDeDatosDeCredencial"/>: solo la leen los roles con escritura y con
/// segundo factor; Consulta, el Usuario de Cliente y toda Sesión Privilegiada reciben
/// <c>null</c> (<see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>).
/// Cada usuario entregado queda antes en la auditoría como <c>AccesoDatoSensible</c>; si no
/// se puede registrar, no se entrega.
/// </para>
/// </summary>
public record ObtenerAccesosPlataformaCaeClienteQuery(Guid ClienteEmpresarialId)
    : IRequest<IReadOnlyList<AccesoPlataformaCaeDto>?>, IConsultaDeDatosDeCredencial;

/// <param name="TieneContrasena">Hay una contraseña guardada (sin descifrarla).</param>
/// <param name="ContrasenaCambiadaPor">Nombre visible de quien la cambió, si sigue siendo alcanzable desde el Tenant.</param>
public record AccesoPlataformaCaeDto(
    Guid EmpresaId,
    string RazonSocial,
    string? Usuario,
    bool TieneContrasena,
    DateTime? ContrasenaCambiadaEnUtc,
    string? ContrasenaCambiadaPor);

public class ObtenerAccesosPlataformaCaeClienteQueryHandler(
    IEmpresasQueryContext dbContext,
    IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios,
    IRegistroAccesoDatoSensibleService registroAcceso)
    : IRequestHandler<ObtenerAccesosPlataformaCaeClienteQuery, IReadOnlyList<AccesoPlataformaCaeDto>?>
{
    public async Task<IReadOnlyList<AccesoPlataformaCaeDto>?> Handle(
        ObtenerAccesosPlataformaCaeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return null;

        // Sin plataforma registrada no hay caja de accesos (maqueta: los accesos cuelgan de ella).
        if (!await dbContext.PlataformasCaeClienteEmpresarial.AnyAsync(p => p.ClienteEmpresarialId == request.ClienteEmpresarialId, cancellationToken))
            return [];

        var empresasParaGestion = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

        var empresas = await (
            from r in dbContext.RelacionesEmpresariales
            where r.ClienteId == request.ClienteEmpresarialId && r.VigenciaHasta == null
            join empresa in dbContext.Empresas.Where(e => e.EsPropia) on r.ProveedoraId equals empresa.Id
            orderby empresa.RazonSocial
            select new { empresa.Id, empresa.RazonSocial })
            .ToListAsync(cancellationToken);

        if (empresasParaGestion is not null)
            empresas = empresas.Where(e => empresasParaGestion.Contains(e.Id)).ToList();

        if (empresas.Count == 0)
            return [];

        var empresaIds = empresas.Select(e => e.Id).ToList();
        var accesos = await dbContext.AccesosPlataformaCaeEmpresa
            .Where(a => a.ClienteEmpresarialId == request.ClienteEmpresarialId && empresaIds.Contains(a.EmpresaId))
            .Select(a => new { a.Id, a.EmpresaId, a.Usuario, a.ContrasenaCambiadaEnUtc, a.ContrasenaCambiadaPorUsuarioId })
            .ToListAsync(cancellationToken);
        var accesoPorEmpresa = accesos.ToDictionary(a => a.EmpresaId);

        var autores = accesos.Where(a => a.ContrasenaCambiadaPorUsuarioId is not null)
            .Select(a => a.ContrasenaCambiadaPorUsuarioId!.Value).Distinct().ToList();
        var nombres = autores.Count == 0
            ? new Dictionary<Guid, string>()
            : await directorioUsuarios.ObtenerNombresVisiblesAsync(autores, cancellationToken);

        // Lectura efectiva del usuario descifrado: queda en la auditoría ANTES de entregarlo, marcada
        // como lectura del usuario para no confundirla con «Mostrar» o «Copiar» la contraseña.
        foreach (var acceso in accesos.Where(a => a.Usuario is not null))
            await registroAcceso.RegistrarLecturaAsync(
                nameof(AccesoPlataformaCaeEmpresa), acceso.Id, nameof(AccesoPlataformaCaeEmpresa.Usuario), cancellationToken);

        return empresas.Select(e =>
        {
            accesoPorEmpresa.TryGetValue(e.Id, out var a);
            string? autor = null;
            if (a?.ContrasenaCambiadaPorUsuarioId is { } autorId)
                nombres.TryGetValue(autorId, out autor);
            return new AccesoPlataformaCaeDto(
                e.Id, e.RazonSocial, a?.Usuario, a?.ContrasenaCambiadaEnUtc is not null, a?.ContrasenaCambiadaEnUtc, autor);
        }).ToList();
    }
}
