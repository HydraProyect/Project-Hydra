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
/// <c>IDataProtector</c> la descifra — no es una consulta de secretos, no
/// necesita <see cref="IConsultaDeSecretosDeTenant"/> ni entrar en la lista
/// de <c>ConsultasDeSecretosMarcadasTests</c>.
///
/// La contraseña solo se obtiene mediante
/// <c>ObtenerCredencialAccesoSubcontrataQuery</c> (esa sí, marcada), invocada
/// como petición explícita y separada — nunca como efecto de abrir esta
/// pantalla.
/// </summary>
public record ObtenerCredencialAccesoSubcontrataSinContrasenaQuery(Guid SubcontrataId) : IRequest<CredencialAccesoSubcontrataSinContrasenaDto?>;

public record CredencialAccesoSubcontrataSinContrasenaDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Notas);

public class ObtenerCredencialAccesoSubcontrataSinContrasenaQueryHandler(
    ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCredencialAccesoSubcontrataSinContrasenaQuery, CredencialAccesoSubcontrataSinContrasenaDto?>
{
    public async Task<CredencialAccesoSubcontrataSinContrasenaDto?> Handle(
        ObtenerCredencialAccesoSubcontrataSinContrasenaQuery request, CancellationToken cancellationToken)
    {
        // Misma puerta que la consulta con contraseña (REC-159): alcance de
        // GESTIÓN, no de lectura — ver ObtenerCredencialAccesoSubcontrataQuery.
        if (!await alcanceDatos.SubcontrataParaGestionVisibleAsync(request.SubcontrataId, cancellationToken))
            return null;

        return await dbContext.CredencialesAccesoSubcontrata
            .Where(c => c.SubcontrataId == request.SubcontrataId)
            .Select(c => new CredencialAccesoSubcontrataSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
