using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerPlataformaCaeCliente;

/// <summary>
/// La parte no secreta de la caja «Plataforma CAE» de Cliente 360: la plataforma del Cliente
/// empresarial, la dirección de su portal y el código de cliente. No lee ninguna credencial:
/// los accesos de cada Empresa van por <c>ObtenerAccesosPlataformaCaeClienteQuery</c>, marcada.
///
/// <para>
/// <c>null</c> = la caja no se pinta: Cliente empresarial fuera de la cartera, o Usuario de
/// Cliente (rol Cliente), que abre la ficha de su propio Cliente empresarial pero para quien
/// la plataforma con la que se le acredita es un artefacto interno de la gestión. El rol
/// Consulta sí la recibe (decisión del 17-10: «Consulta: solo plataforma y Abrir portal»).
/// </para>
///
/// <para>
/// El código de cliente solo viaja a los roles con escritura del Tenant propietario y fuera
/// de toda Sesión Privilegiada (decisión de la coordinación, 2026-10-18): Consulta, soporte e
/// impersonación lo reciben <c>null</c>, aunque la interfaz ya lo oculte con
/// <c>SoloConEscritura</c>. Ocultarlo solo en la pantalla dejaría el dato en la respuesta.
/// </para>
/// </summary>
public record ObtenerPlataformaCaeClienteQuery(Guid ClienteEmpresarialId) : IRequest<PlataformaCaeClienteDto?>;

/// <param name="Registrada">False: el Cliente empresarial aún no tiene plataforma registrada.</param>
/// <param name="CodigoCliente">Solo para los roles con escritura; <c>null</c> para el resto.</param>
public record PlataformaCaeClienteDto(bool Registrada, string? Nombre, string? UrlPortal, string? CodigoCliente)
{
    /// <summary>Token de concurrencia que la caja reenvía al guardar; <c>null</c> sin plataforma registrada.</summary>
    public Guid? Version { get; init; }

    /// <summary>
    /// Sin plataforma registrada en el Cliente empresarial: las plataformas CAE en las que se acreditan ya sus Centros
    /// (canal de tipo Plataforma de cada Centro, por el nombre de su proveedor o, sin él, por su etiqueta). Revisión 1:1
    /// del 2026-10-21: la caja decía «Sin plataforma CAE registrada» mientras un Centro del mismo Cliente empresarial
    /// tenía un bloqueo de esa plataforma. Solo nombres: ni dirección ni credenciales del canal. Vacía con la
    /// plataforma registrada, que es la que manda.
    /// </summary>
    public IReadOnlyList<string> PlataformasDeSusCentros { get; init; } = [];
}

public class ObtenerPlataformaCaeClienteQueryHandler(
    IEmpresasQueryContext dbContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext)
    : IRequestHandler<ObtenerPlataformaCaeClienteQuery, PlataformaCaeClienteDto?>
{
    public async Task<PlataformaCaeClienteDto?> Handle(ObtenerPlataformaCaeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return null;

        if (!await dbContext.Empresas.AnyAsync(e => e.Id == request.ClienteEmpresarialId, cancellationToken))
            return null;

        var plataforma = await dbContext.PlataformasCaeClienteEmpresarial
            .Where(p => p.ClienteEmpresarialId == request.ClienteEmpresarialId)
            .Select(p => new PlataformaCaeClienteDto(true, p.Nombre, p.UrlPortal, p.CodigoCliente) { Version = p.Version })
            .FirstOrDefaultAsync(cancellationToken);

        if (plataforma is null)
            return new PlataformaCaeClienteDto(false, null, null, null)
            {
                PlataformasDeSusCentros = await PlataformasDeSusCentrosAsync(request.ClienteEmpresarialId, cancellationToken)
            };

        return await VeElCodigoDeClienteAsync(cancellationToken) ? plataforma : plataforma with { CodigoCliente = null };
    }

    private async Task<IReadOnlyList<string>> PlataformasDeSusCentrosAsync(Guid clienteEmpresarialId, CancellationToken cancellationToken)
    {
        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteEmpresarialId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        if (centroIds.Count == 0) return [];

        var canales = await centrosContext.CanalesGestionDocumental
            .Where(c => centroIds.Contains(c.CentroId) && c.Tipo == TipoCanalGestion.Plataforma)
            .Select(c => new { c.ProveedorPlataformaCaeId, c.EtiquetaProposito })
            .ToListAsync(cancellationToken);
        if (canales.Count == 0) return [];

        var proveedorIds = canales.Where(c => c.ProveedorPlataformaCaeId is not null).Select(c => c.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var nombres = proveedorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await proveedoresContext.ProveedoresPlataformaCae
                .Where(p => proveedorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return canales
            .Select(c => c.ProveedorPlataformaCaeId is { } id && nombres.TryGetValue(id, out var nombre) ? nombre : c.EtiquetaProposito)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<bool> VeElCodigoDeClienteAsync(CancellationToken cancellationToken) =>
        await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is null
        && await currentUserService.ObtenerRolEfectivoAsync() is { } rol
        && AutorizacionEscrituraBehavior<object, object>.RolesConEscritura.Contains(rol);
}
