using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrataSinContrasena;

/// <summary>
/// Los campos no sensibles de la credencial de acceso de una Subcontrata, para
/// precargar el formulario de edición sin tocar la contraseña (DEC-53/DEC-62,
/// gemela de <c>ObtenerCredencialAccesoEmpresaSinContrasenaQuery</c>): la
/// proyección no incluye <c>Contrasena</c>, así que ni EF Core la lee ni el
/// <c>IDataProtector</c> la descifra.
///
/// Sí descifra el <c>Usuario</c> (cifrado en reposo igual que la contraseña,
/// <c>CaeManagerDbContext</c>), así que está marcada con
/// <see cref="IConsultaDeDatosDeCredencial"/>: solo la leen los roles con
/// escritura, nunca en una Sesión Privilegiada, y cada lectura queda en la
/// auditoría como <c>AccesoDatoSensible</c>.
///
/// La contraseña solo se obtiene mediante
/// <c>ObtenerCredencialAccesoSubcontrataQuery</c> (esa sí, marcada), invocada
/// como petición explícita y separada — nunca como efecto de abrir esta
/// pantalla.
/// </summary>
public record ObtenerCredencialAccesoSubcontrataSinContrasenaQuery(Guid SubcontrataId) : IRequest<CredencialAccesoSubcontrataSinContrasenaDto?>, IConsultaDeDatosDeCredencial;

public record CredencialAccesoSubcontrataSinContrasenaDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Notas);

public class ObtenerCredencialAccesoSubcontrataSinContrasenaQueryHandler(
    ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos, IRegistroAccesoDatoSensibleService registroAcceso)
    : IRequestHandler<ObtenerCredencialAccesoSubcontrataSinContrasenaQuery, CredencialAccesoSubcontrataSinContrasenaDto?>
{
    public async Task<CredencialAccesoSubcontrataSinContrasenaDto?> Handle(
        ObtenerCredencialAccesoSubcontrataSinContrasenaQuery request, CancellationToken cancellationToken)
    {
        // Misma puerta que la consulta con contraseña (REC-159): alcance de
        // GESTIÓN, no de lectura — ver ObtenerCredencialAccesoSubcontrataQuery.
        if (!await alcanceDatos.SubcontrataParaGestionVisibleAsync(request.SubcontrataId, cancellationToken))
            return null;

        var fila = await dbContext.CredencialesAccesoSubcontrata
            .Where(c => c.SubcontrataId == request.SubcontrataId)
            .Select(c => new { c.Id, Dto = new CredencialAccesoSubcontrataSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas) })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila is null)
            return null;

        // Lectura efectiva: queda en la auditoría ANTES de entregar el dato, y si
        // no se puede registrar no se entrega (IRegistroAccesoDatoSensibleService).
        await registroAcceso.RegistrarAsync("CredencialAccesoSubcontrata", fila.Id, cancellationToken);
        return fila.Dto;
    }
}
