using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;

/// <summary>
/// Los campos no sensibles de la credencial de acceso de una Empresa, para
/// precargar el formulario de edición sin tocar la contraseña (DEC-53/DEC-62):
/// la proyección no incluye <c>Contrasena</c>, así que ni EF Core la lee ni
/// el <c>IDataProtector</c> la descifra.
///
/// Sí descifra el <c>Usuario</c> (cifrado en reposo igual que la contraseña,
/// <c>CaeManagerDbContext</c>), así que solo la leen los roles con escritura:
/// está marcada con <see cref="IConsultaDeDatosDeCredencial"/>, no con
/// <see cref="IConsultaDeSecretosDeTenant"/>, porque el formulario guarda lo
/// que precarga y denegarla en una Sesión Privilegiada vaciaría la
/// credencial al guardar.
///
/// La contraseña solo se obtiene mediante
/// <c>ObtenerCredencialAccesoEmpresaQuery</c> (esa sí, marcada), invocada
/// como petición explícita y separada — nunca como efecto de abrir esta
/// pantalla.
/// </summary>
public record ObtenerCredencialAccesoEmpresaSinContrasenaQuery(Guid EmpresaId) : IRequest<CredencialAccesoEmpresaSinContrasenaDto?>, IConsultaDeDatosDeCredencial;

public record CredencialAccesoEmpresaSinContrasenaDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Notas);

public class ObtenerCredencialAccesoEmpresaSinContrasenaQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCredencialAccesoEmpresaSinContrasenaQuery, CredencialAccesoEmpresaSinContrasenaDto?>
{
    public async Task<CredencialAccesoEmpresaSinContrasenaDto?> Handle(
        ObtenerCredencialAccesoEmpresaSinContrasenaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        return await dbContext.CredencialesAccesoEmpresa
            .Where(c => c.EmpresaId == request.EmpresaId)
            .Select(c => new CredencialAccesoEmpresaSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
